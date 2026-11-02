using System.Globalization;
using Forge.Connectivity.Contracts;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Forge.Host.Connectivity;

/// <summary>
/// OPC UA through the OPC Foundation's .NET client (owner decisions 2026-11-01). Reads the state node and, when set, the part and
/// scrap counter nodes. Security "None" connects without certificates; "Sign" or "SignAndEncrypt" uses Forge's application
/// certificate (created on first use in Forge:Connectivity:PkiFolder) and trusts a server certificate only when it is in the trusted
/// folder, or when trustServer is true. A user name and password are used when set; otherwise the session is anonymous. Read only.
/// </summary>
public sealed class OpcUaConnector(IConfiguration configuration, ILogger<OpcUaConnector> logger) : IMachineConnector, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ApplicationConfiguration? application;

    public ConnectorKind Kind => ConnectorKind.OpcUa;

    public void Dispose() => gate.Dispose();

    public string PkiFolder => configuration["Forge:Connectivity:PkiFolder"] is { Length: > 0 } folder
        ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JISRN Forge", "pki");

    public async Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = ConnectorCatalog.Setting(connection, "endpoint")!;
        var security = ConnectorCatalog.Setting(connection, "security") ?? "None";
        var trustServer = string.Equals(ConnectorCatalog.Setting(connection, "trustServer"), "true", StringComparison.OrdinalIgnoreCase);
        var user = ConnectorCatalog.Setting(connection, "username");
        var password = ConnectorCatalog.Setting(connection, "password");
        try
        {
            var config = await ConfigurationAsync(security != "None", ct);
            var described = CoreClientUtils.SelectEndpoint(config, url, security != "None", 5000);
            if (security == "SignAndEncrypt" && described.SecurityMode != MessageSecurityMode.SignAndEncrypt)
                throw new MachineConnectorException($"The OPC UA server at {url} does not offer {security}.");
            var endpoint = new ConfiguredEndpoint(null, described, EndpointConfiguration.Create(config));
            void Validate(CertificateValidator sender, CertificateValidationEventArgs e)
            {
                if (trustServer && e.Error.StatusCode == Opc.Ua.StatusCodes.BadCertificateUntrusted) e.Accept = true;
            }

            config.CertificateValidator.CertificateValidation += Validate;
            try
            {
                IUserIdentity identity = user is null ? new UserIdentity(new AnonymousIdentityToken()) : new UserIdentity(user, password ?? string.Empty);
                using var session = await Session.Create(config, endpoint, false, "Forge " + connection.MachineCode, 30_000, identity, null, ct);
                var nodes = new ReadValueIdCollection();
                var names = new List<string>();
                foreach (var name in new[] { "stateNode", "countNode", "scrapNode" })
                {
                    if (ConnectorCatalog.Setting(connection, name) is not { } node) continue;
                    nodes.Add(new ReadValueId { NodeId = NodeId.Parse(node), AttributeId = Attributes.Value });
                    names.Add(name);
                }

                var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, nodes, ct);
                var values = new Dictionary<string, DataValue>(StringComparer.Ordinal);
                for (var i = 0; i < names.Count && i < response.Results.Count; i++) values[names[i]] = response.Results[i];
                await session.CloseAsync(ct);
                return [Reading(values, ConnectorCatalog.Setting(connection, "runningValue"), now)];
            }
            finally
            {
                config.CertificateValidator.CertificateValidation -= Validate;
            }
        }
        catch (ServiceResultException ex)
        {
            throw new MachineConnectorException($"OPC UA server at {url}: {ex.StatusCode} {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new MachineConnectorException($"An OPC UA node id is not valid: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MachineConnectorException)
        {
            throw new MachineConnectorException($"OPC UA server at {url} could not be read: {ex.Message}");
        }
    }

    /// <summary>Turns the read values into a reading: running when the state equals runningValue (default true or 1).</summary>
    public static MachineReading Reading(IReadOnlyDictionary<string, DataValue> values, string? runningValue, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue("stateNode", out var state) || !StatusCode.IsGood(state.StatusCode))
            return new MachineReading(now, MachineState.Offline);
        var text = Convert.ToString(state.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        var running = runningValue is { Length: > 0 }
            ? string.Equals(text, runningValue, StringComparison.OrdinalIgnoreCase)
            : text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
        long? Count(string name) => values.TryGetValue(name, out var v) && StatusCode.IsGood(v.StatusCode) && v.Value is IConvertible c
            ? Convert.ToInt64(c, CultureInfo.InvariantCulture) : null;
        return new MachineReading(now, running ? MachineState.Running : MachineState.Stopped, Count("countNode"), Count("scrapNode"));
    }

    private async Task<ApplicationConfiguration> ConfigurationAsync(bool needsCertificate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (application is null)
            {
                var pki = PkiFolder;
                var config = new ApplicationConfiguration
                {
                    ApplicationName = "JISRN Forge",
                    ApplicationUri = Utils.Format("urn:{0}:JISRNForge", System.Net.Dns.GetHostName()),
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=JISRN Forge, O=JISRN" },
                        TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                        TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuers") },
                        RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                        AutoAcceptUntrustedCertificates = false,
                        AddAppCertToTrustedStore = false,
                    },
                    TransportConfigurations = new TransportConfigurationCollection(),
                    TransportQuotas = new TransportQuotas { OperationTimeout = 10_000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 30_000 },
                    TraceConfiguration = new TraceConfiguration(),
                };
                await config.Validate(ApplicationType.Client);
                application = config;
            }

            if (needsCertificate)
            {
                var instance = new ApplicationInstance(application);
                if (!await instance.CheckApplicationInstanceCertificate(true, 2048))
                    logger.LogWarning("Forge's OPC UA application certificate could not be created in {Folder}.", PkiFolder);
            }

            return application;
        }
        finally
        {
            gate.Release();
        }
    }
}
