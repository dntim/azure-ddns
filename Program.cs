using System.Net;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureDdns;

public static class Program
{
	public static async Task Main(string[] args)
	{
		using IHost host = CreateHostBuilder(args).Build();
		var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
		logger.LogInformation("Starting Azure DDNS Updater v1.0.0");
		await host.RunAsync();
	}

	private static IHostBuilder CreateHostBuilder(string[] args)
	{
		return Host.CreateDefaultBuilder(args)
			.ConfigureAppConfiguration((_, config) =>
			{
				config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
				config.AddEnvironmentVariables();
			})
			.ConfigureServices((context, services) =>
			{
				services.Configure<DDNSConfiguration>(context.Configuration.GetSection(DDNSConfiguration.SectionName));

				services.AddHttpClient();
				services.AddSingleton<TokenCredential>(sp =>
				{
					var options = sp.GetRequiredService<IOptions<DDNSConfiguration>>().Value;
					var authLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AzureAuth");
					return AzureCredentialFactory.Create(options, authLogger);
				});
				services.AddHostedService<AzureDnsUpdateWorker>();
			})
			.ConfigureLogging(logging =>
			{
				logging.ClearProviders();
				logging.AddSimpleConsole(options =>
				{
					options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
					options.SingleLine = true;
				});
				logging.SetMinimumLevel(LogLevel.Information);
			});
	}
}

internal static class AzureCredentialFactory
{
	public static TokenCredential Create(DDNSConfiguration config, ILogger logger)
	{
		var mode = (config.AuthMode ?? "Default").Trim();

		if (mode.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase))
		{
			var tenantId = GetSetting(
				config.TenantId,
				envVarName: "DDNS__TENANTID",
				defaultSecretPath: "/run/secrets/ddns_azure_tenant_id");

			var clientId = GetSetting(
				config.ClientId,
				envVarName: "DDNS__CLIENTID",
				defaultSecretPath: "/run/secrets/ddns_azure_client_id");

			var clientSecret = GetSetting(
				config.ClientSecret,
				envVarName: "DDNS__CLIENTSECRET",
				overrideSecretPath: config.ClientSecretFile,
				defaultSecretPath: "/run/secrets/ddns_azure_client_secret");

			if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
			{
				throw new InvalidOperationException(
					"ClientSecret auth selected, but TenantId/ClientId/ClientSecret are missing. Configure DDNS__TenantId, DDNS__ClientId and DDNS__ClientSecret (or Docker secrets)."
				);
			}

			logger.LogInformation("Azure auth mode: ClientSecret");
			return new ClientSecretCredential(tenantId, clientId, clientSecret);
		}

		if (mode.Equals("ManagedIdentity", StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation("Azure auth mode: ManagedIdentity");
			return string.IsNullOrWhiteSpace(config.ManagedIdentityClientId)
				? new ManagedIdentityCredential()
				: new ManagedIdentityCredential(config.ManagedIdentityClientId);
		}

		if (mode.Equals("Environment", StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation("Azure auth mode: Environment");
			return new EnvironmentCredential();
		}

		logger.LogInformation("Azure auth mode: Default");
		return new DefaultAzureCredential(new DefaultAzureCredentialOptions
		{
			ManagedIdentityClientId = string.IsNullOrWhiteSpace(config.ManagedIdentityClientId) ? null : config.ManagedIdentityClientId
		});
	}

	private static string? GetSetting(string? directValue, string envVarName, string? overrideSecretPath = null, string? defaultSecretPath = null)
	{
		if (!string.IsNullOrWhiteSpace(directValue))
		{
			return directValue;
		}

		if (!string.IsNullOrWhiteSpace(overrideSecretPath))
		{
			var secret = ReadSecret(overrideSecretPath);
			if (!string.IsNullOrWhiteSpace(secret))
			{
				return secret;
			}
		}

		if (!string.IsNullOrWhiteSpace(defaultSecretPath))
		{
			var secret = ReadSecret(defaultSecretPath);
			if (!string.IsNullOrWhiteSpace(secret))
			{
				return secret;
			}
		}

		return Environment.GetEnvironmentVariable(envVarName);
	}

	private static string? ReadSecret(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}

			var content = File.ReadAllText(path).Trim();
			return string.IsNullOrWhiteSpace(content) ? null : content;
		}
		catch
		{
			return null;
		}
	}
}

public sealed class AzureDnsUpdateWorker : BackgroundService
{
	private readonly ILogger<AzureDnsUpdateWorker> _logger;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IOptionsMonitor<DDNSConfiguration> _configuration;
	private readonly TokenCredential _credential;

	public AzureDnsUpdateWorker(
		ILogger<AzureDnsUpdateWorker> logger,
		IHttpClientFactory httpClientFactory,
		IOptionsMonitor<DDNSConfiguration> configuration,
		TokenCredential credential)
	{
		_logger = logger;
		_httpClientFactory = httpClientFactory;
		_configuration = configuration;
		_credential = credential;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Azure DNS updater service started.");

		while (!stoppingToken.IsCancellationRequested)
		{
			var config = _configuration.CurrentValue;
			var interval = TimeSpan.FromSeconds(Math.Max(30, config.UpdateIntervalSeconds));

			try
			{
				await UpdateDnsRecordsAsync(config, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Unhandled error during Azure DNS update cycle.");
			}

			try
			{
				await Task.Delay(interval, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	private async Task UpdateDnsRecordsAsync(DDNSConfiguration config, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(config.SubscriptionId) ||
			string.IsNullOrWhiteSpace(config.ResourceGroupName) ||
			string.IsNullOrWhiteSpace(config.ZoneName))
		{
			_logger.LogError(
				"Missing required Azure DNS configuration. Ensure DDNS__SubscriptionId, DDNS__ResourceGroupName, and DDNS__ZoneName are set.");
			return;
		}

		if (config.Hostnames.Length == 0)
		{
			_logger.LogWarning("No hostnames configured. Set DDNS__Hostnames__0 (and more) to enable updates.");
			return;
		}

		var publicIp = await ResolvePublicIPv4Async(config, cancellationToken);
		_logger.LogInformation("Resolved current public IPv4 address: {PublicIp}", publicIp);

		var armClient = new ArmClient(_credential, config.SubscriptionId);
		var zoneResourceId = DnsZoneResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroupName, config.ZoneName);
		var zoneResource = armClient.GetDnsZoneResource(zoneResourceId);
		var aRecordCollection = zoneResource.GetDnsARecords();

		foreach (var hostname in config.Hostnames)
		{
			var recordSetName = ToRecordSetName(hostname, config.ZoneName);
			await CreateOrUpdateARecordAsync(aRecordCollection, hostname, recordSetName, publicIp, config.TtlSeconds, cancellationToken);
		}
	}

	private async Task<IPAddress> ResolvePublicIPv4Async(DDNSConfiguration config, CancellationToken cancellationToken)
	{
		using var client = _httpClientFactory.CreateClient();
		client.Timeout = TimeSpan.FromSeconds(10);

		var urls = (config.IpLookupUrls.Length == 0 ? DDNSConfiguration.DefaultIpLookupUrls : config.IpLookupUrls)
			.Where(static x => !string.IsNullOrWhiteSpace(x))
			.ToArray();

		if (urls.Length == 0)
		{
			throw new InvalidOperationException("No IP lookup URLs are configured.");
		}

		foreach (var url in urls)
		{
			for (var attempt = 1; attempt <= 3; attempt++)
			{
				try
				{
					var response = await client.GetStringAsync(url, cancellationToken);
					var candidate = response.Trim();
					var token = candidate.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

					if (token is not null && IPAddress.TryParse(token, out var ipAddress) && ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
					{
						return ipAddress;
					}

					_logger.LogWarning("IP lookup endpoint returned unexpected value. Url: {Url}, Value: {Value}", url, candidate);
				}
				catch (Exception ex) when (attempt < 3)
				{
					var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
					_logger.LogWarning(ex, "IP lookup failed (attempt {Attempt}/3) for {Url}. Retrying in {DelaySeconds} seconds.", attempt, url, delay.TotalSeconds);
					await Task.Delay(delay, cancellationToken);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "IP lookup failed for {Url}", url);
				}
			}
		}

		throw new InvalidOperationException("Failed to resolve public IPv4 address from configured lookup endpoints.");
	}

	private async Task CreateOrUpdateARecordAsync(
		DnsARecordCollection recordCollection,
		string configuredHostname,
		string recordSetName,
		IPAddress publicIp,
		long ttlSeconds,
		CancellationToken cancellationToken)
	{
		var exists = await recordCollection.ExistsAsync(recordSetName, cancellationToken);

		if (!exists)
		{
			var createData = new DnsARecordData
			{
				TtlInSeconds = ttlSeconds
			};

			createData.DnsARecords.Add(new DnsARecordInfo
			{
				IPv4Address = publicIp
			});

			await recordCollection.CreateOrUpdateAsync(
				WaitUntil.Completed,
				recordSetName,
				createData,
				ifMatch: null,
				ifNoneMatch: null,
				cancellationToken);
			_logger.LogInformation("Created A record for {Hostname} ({RecordSetName}) -> {PublicIp}", configuredHostname, recordSetName, publicIp);
			return;
		}

		var existingRecord = await recordCollection.GetAsync(recordSetName, cancellationToken);
		var data = existingRecord.Value.Data;
		var existingIps = data.DnsARecords.Select(static x => x.IPv4Address).Where(static x => x is not null).ToArray();
		var alreadyUpToDate = existingIps.Length == 1 && existingIps[0]!.Equals(publicIp) && data.TtlInSeconds == ttlSeconds;

		if (alreadyUpToDate)
		{
			_logger.LogInformation("No change for {Hostname} ({RecordSetName}); already points to {PublicIp}", configuredHostname, recordSetName, publicIp);
			return;
		}

		data.TtlInSeconds = ttlSeconds;
		data.DnsARecords.Clear();
		data.DnsARecords.Add(new DnsARecordInfo
		{
			IPv4Address = publicIp
		});

		await existingRecord.Value.UpdateAsync(data, ifMatch: null, cancellationToken);
		_logger.LogInformation("Updated A record for {Hostname} ({RecordSetName}) -> {PublicIp}", configuredHostname, recordSetName, publicIp);
	}

	private static string ToRecordSetName(string configuredHostname, string zoneName)
	{
		var normalized = configuredHostname.Trim().TrimEnd('.');
		var normalizedZone = zoneName.Trim().TrimEnd('.');

		if (string.Equals(normalized, "@", StringComparison.Ordinal))
		{
			return "@";
		}

		if (string.Equals(normalized, normalizedZone, StringComparison.OrdinalIgnoreCase))
		{
			return "@";
		}

		var suffix = $".{normalizedZone}";
		if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
		{
			return normalized[..^suffix.Length];
		}

		return normalized;
	}
}
