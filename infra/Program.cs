using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Yaml;
using YARG.Online.Infrastructure;
using Cloudflare = Pulumi.Cloudflare;
using Random = Pulumi.Random;
using K8sDeployment = Pulumi.Kubernetes.Apps.V1.Deployment;
using HelmRelease = Pulumi.Kubernetes.Helm.V3.Release;
using HelmReleaseArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.ReleaseArgs;
using V3RepositoryOptsArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.RepositoryOptsArgs;

return await Deployment.RunAsync(() =>
{
    var config = new Pulumi.Config();
    var provisionOke = config.GetBoolean("provisionOkeCluster") ?? false;

    // The `local` stack attaches to an existing kube context; OCI stacks provision an
    // OKE cluster first and drive the Kubernetes provider from its generated kubeconfig.
    OracleResources? oci = null;
    Provider k8s;
    if (provisionOke)
    {
        oci = Oracle.Provision(config);

        // H-4: the OKE API server is private. Open an ssh local-port-forward
        // (`-L 6443:<api-ip>:6443`) through the OCI Bastion's PORT_FORWARDING
        // session as part of the dependency graph, then patch the generated
        // kubeconfig so it talks to 127.0.0.1:6443. The private key path is
        // consumed locally by ssh.exe — only the matching pubkey enters Pulumi
        // state (via the bastion session resource).
        var sshPrivateKeyPath = config.Require("sshPrivateKeyPath");

        var tunnelCreate = Output.Tuple(oci.BastionSessionId, oci.BastionHost, oci.ApiEndpointIp)
            .Apply(t =>
            {
                var (sessionId, bastionHost, apiIp) = t;
                return BuildTunnelCreateScript(sshPrivateKeyPath, sessionId, bastionHost, apiIp);
            });

        var tunnel = new Pulumi.Command.Local.Command("oke-api-tunnel",
            new Pulumi.Command.Local.CommandArgs
            {
                Create = tunnelCreate,
                Delete = BuildTunnelDeleteScript(),
                // Windows-only — matches the operator's environment. If this ever
                // moves to CI/Linux, swap Start-Process/Stop-Process for `ssh -fN`
                // + `pkill -F .tunnel.pid`. Mechanically identical.
                Interpreter = { "powershell.exe", "-NoProfile", "-Command" },
                // Re-run Create only when the session is replaced (every 3h on TTL
                // expiry, or on config change). Between rotations the detached ssh
                // process persists across `pulumi up` invocations.
                Triggers = { (Input<object>)oci.BastionSessionId.Apply(s => (object)s) },
            });

        // Rewrite the kubeconfig's `server:` URL to point at the local forward
        // (127.0.0.1:6443) and pin `tls-server-name:` to the original API host so
        // client-go's TLS verification still matches OKE's certificate SANs.
        var patchedKubeconfig = oci.KubeConfig.Apply(PatchKubeconfigForPortForward);

        k8s = new Provider("oci", new ProviderArgs { KubeConfig = patchedKubeconfig },
            new CustomResourceOptions { DependsOn = { tunnel } });
    }
    else
    {
        k8s = new Provider("local", new ProviderArgs { Context = config.Require("kubeContext") });
    }

    var providerOpts = new CustomResourceOptions { Provider = k8s };

    // --- Cloudflare provider (oci-prod only) ---
    //
    // Hoisted ahead of EnvoyGateway.Deploy so the Gateway module can issue an
    // Origin CA certificate against the same provider. The provider's API
    // token must carry, at minimum: Zone.DNS:Edit, Zone.SSL and Certificates:Edit,
    // Account.Cloudflare Tunnel:Edit, Account Settings:Read.
    Cloudflare.Provider? cfProvider = null;
    var cfToken = config.GetSecret("cloudflareApiToken");
    var cfApexDomain = config.Get("cloudflareApexDomain");
    if (provisionOke && cfToken is not null)
    {
        cfProvider = new Cloudflare.Provider("cloudflare", new Cloudflare.ProviderArgs
        {
            ApiToken = cfToken,
        });
    }

    // --- kube-prometheus-stack ---
    //
    // Installed first so its Prometheus Operator CRDs (ServiceMonitor, PodMonitor,
    // etc.) exist before any other chart tries to create those resources. Agones,
    // for example, ships its own ServiceMonitor when metrics.serviceMonitor.enabled
    // is true and would fail with "resource mapping not found" on a fresh cluster
    // if it raced the monitoring install.
    //
    // Sizing, storage class, and node selectors live in
    // infra/values/{common,local,oci-prod}/monitoring.yaml. The release name
    // "monitoring" is a code-level const in Monitoring.cs because it's referenced
    // by the lobbies/game ServiceMonitor/PodMonitor selectors (release=monitoring)
    // and by in-cluster Service DNS (monitoring-grafana, …).
    var monitoringNamespaceName = "monitoring";
    var monitoring = Monitoring.Deploy(config, k8s, monitoringNamespaceName, providerOpts);

    // --- CloudNativePG (operator + Cluster + Pooler) ---
    //
    // Throws on oci-prod until the dedicated `workload=database` node pool is
    // provisioned in Oracle.cs — see Postgres.cs for the guard. On local, this
    // brings up a single-instance PG 18 in the `database` namespace with a
    // PgBouncer pooler in front of it. Opt out with `deployPostgres=false`
    // when bringing up a stack that doesn't need the database (e.g. iterating
    // on gateway/agones without paying the CNPG install time).
    var deployPostgres = config.GetBoolean("deployPostgres") ?? true;
    var postgres = deployPostgres
        ? Postgres.Deploy(
            config,
            k8s,
            provisionOke,
            cnpgNamespaceName: "cnpg-system",
            databaseNamespaceName: "database",
            monitoringDependency: monitoring.Release,
            providerOpts: providerOpts)
        : null;

    // Envoy Gateway: CRDs, controller, `envoy` GatewayClass, `main` Gateway, and
    // ServiceMonitor/PodMonitor bridges into kube-prometheus-stack. On OCI also
    // installs the `oci-nlb` EnvoyProxy CR that pins the Gateway to the cluster's
    // OCI Network Load Balancer (reserved IP + subnet + NSG). Controller chart
    // values live in infra/values/{common,local,oci-prod}/envoy-gateway.yaml.
    var envoyGateway = EnvoyGateway.Deploy(
        config, k8s, oci,
        monitoringNamespace: monitoringNamespaceName,
        monitoringDependency: monitoring.Release,
        cfProvider: cfProvider,
        cfApexDomain: cfApexDomain,
        providerOpts);

    // Agones operator + CRDs. Must run after monitoring — the chart's
    // metrics ServiceMonitor depends on the Prometheus Operator CRDs that
    // kube-prometheus-stack installs. Chart values (replicas, allocator
    // service type, ping toggle, ServiceMonitor toggle, nodeSelectors) live
    // in infra/values/{common,local,oci-prod}/agones.yaml.
    var agones = Agones.Deploy(
        config,
        k8s,
        agonesNamespaceName: "agones-system",
        monitoringDependency: monitoring.Release,
        providerOpts: providerOpts);

    var isLocal = Deployment.Instance.StackName == "local";

    string? registryHostname = null;

    if (isLocal)
    {
        registryHostname = config.Require("registryHostname");
        var registryUsername = config.Require("registryUsername");
        var registryPassword = config.RequireSecret("registryPassword");
        var registryStorageSize = config.Get("registryStorageSize") ?? "20Gi";

        var registryNamespaceName = "registry";

        var registryNs = new Namespace("registry-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = registryNamespaceName },
        }, providerOpts);

        // Distribution registry expects htpasswd-style "<username>:<bcrypt-hash>".
        // BCrypt.Net-Next output is compatible with htpasswd's bcrypt mode.
        // twuni chart v2.2.3 has no `existingSecret` key — it templates a Secret
        // from `secrets.htpasswd` directly, so we pass the hash inline.
        var htpasswdContents = registryPassword.Apply(pw =>
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(pw, workFactor: 10);
            return $"{registryUsername}:{hash}";
        });

        // helm.twun.io was retired; the chart now lives at twuni.github.io.
        // V3 Release (same Helm SDK path as the controller) — V4 Chart's
        // network path failed DNS even before the host migration.
        var registry = new HelmRelease("registry", new HelmReleaseArgs
        {
            Name = "registry",  // lock release name → Service is "registry-docker-registry"
            Chart = "docker-registry",
            Version = "2.2.3",
            RepositoryOpts = new V3RepositoryOptsArgs
            {
                Repo = "https://twuni.github.io/docker-registry.helm",
            },
            Namespace = registryNs.Metadata.Apply(m => m.Name!),
            Values = new InputMap<object>
            {
                ["persistence"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["size"] = registryStorageSize,
                    ["storageClass"] = "local-path",
                },
                ["secrets"] = new Dictionary<string, object>
                {
                    ["htpasswd"] = htpasswdContents,
                },
                ["ingress"] = new Dictionary<string, object> { ["enabled"] = false },
                ["configData"] = new Dictionary<string, object>
                {
                    ["storage"] = new Dictionary<string, object>
                    {
                        // Keep chart default cache config; add delete API support.
                        ["cache"] = new Dictionary<string, object>
                        {
                            ["blobdescriptor"] = "inmemory",
                        },
                        ["delete"] = new Dictionary<string, object> { ["enabled"] = true },
                    },
                },
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            DependsOn = { registryNs },
        });

        var registryRouteYaml = $@"
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: registry
  namespace: {registryNamespaceName}
spec:
  parentRefs:
    - name: main
      namespace: {envoyGateway.Namespace}
  hostnames:
    - {registryHostname}
  rules:
    - matches:
        - path:
            type: PathPrefix
            value: /
      backendRefs:
        - name: registry-docker-registry
          port: 5000
";

        var registryRoute = new ConfigGroup("registry-route", new ConfigGroupArgs
        {
            Yaml = registryRouteYaml,
        }, new ComponentResourceOptions
        {
            Provider = k8s,
            DependsOn = { registry, envoyGateway.Gateway },
        });
    }

    // Cloudflare front-door: external-dns syncs Cloudflare DNS records straight
    // from the Gateway API HTTPRoutes — hostnames live only in the app charts,
    // and the record targets the Gateway's address (the NLB's reserved IP).
    // Only deploys when the API token is configured. Chart values live in
    // infra/values/oci-prod/external-dns.yaml.
    if (provisionOke && cfToken is not null)
    {
        ExternalDns.Deploy(
            config,
            k8s,
            cloudflareApiToken: cfToken,
            envoyGatewayCrds: envoyGateway.Crds,
            providerOpts: providerOpts);
    }

    // Dashboards (downloaded by Download-Dashboards.ps1) become labeled
    // ConfigMaps in the monitoring namespace; the Grafana sidecar picks them
    // up automatically. If the directory is empty (a developer hasn't run the
    // download script), Grafana still has its chart-bundled dashboards.
    Monitoring.InstallDashboards(k8s, monitoringNamespaceName, monitoring.Release);

    if (provisionOke)
    {
        // --- Cloudflare Tunnel + cloudflared (oci-prod only) ---
        //
        // Outbound-only path into Grafana. Cloudflare Access (configured once
        // out-of-band in the Zero Trust dashboard) authenticates at the edge.
        // Reuses the cfProvider hoisted above (same token as external-dns and
        // the Envoy Gateway Origin CA cert).
        var cfAccountId = config.Get("cloudflareAccountId");
        var cfZoneId = config.Get("cloudflareZoneId");
        var grafanaHostname = config.Get("grafanaHostname");

        if (cfProvider is not null && cfAccountId is not null && cfZoneId is not null
            && grafanaHostname is not null)
        {
            var cfOpts = new CustomResourceOptions { Provider = cfProvider };
            // Invokes (data sources) need their own options bag — CustomResourceOptions
            // applies to resource creations only, so Invoke calls fall through to a
            // default Cloudflare provider with no API token unless explicitly routed.
            var cfInvokeOpts = new InvokeOptions { Provider = cfProvider };

            // Tunnel secret is the password Cloudflare uses to derive the
            // connector token. Generated once via Pulumi.Random so it stays
            // stable across `pulumi up` runs and the tunnel isn't recreated.
            // Base64 length 44 = 32 raw bytes, matching what cloudflared expects.
            var tunnelSecretBytes = new Random.RandomBytes("grafana-tunnel-secret",
                new Random.RandomBytesArgs { Length = 32 });
            var tunnelSecret = tunnelSecretBytes.Base64;

            var tunnel = new Cloudflare.ZeroTrustTunnelCloudflared("grafana-tunnel",
                new Cloudflare.ZeroTrustTunnelCloudflaredArgs
                {
                    AccountId = cfAccountId,
                    Name = "grafana",
                    // Remote-managed config — the ingress rules below live in
                    // Cloudflare's API, not in a local config.yaml on the connector.
                    ConfigSrc = "cloudflare",
                    TunnelSecret = tunnelSecret,
                }, cfOpts);

            _ = new Cloudflare.ZeroTrustTunnelCloudflaredConfig("grafana-tunnel-config",
                new Cloudflare.ZeroTrustTunnelCloudflaredConfigArgs
                {
                    AccountId = cfAccountId,
                    TunnelId = tunnel.Id,
                    Config = new Cloudflare.Inputs.ZeroTrustTunnelCloudflaredConfigConfigArgs
                    {
                        Ingresses =
                        {
                            new Cloudflare.Inputs.ZeroTrustTunnelCloudflaredConfigConfigIngressArgs
                            {
                                Hostname = grafanaHostname,
                                Service = $"http://monitoring-grafana.{monitoringNamespaceName}.svc.cluster.local:80",
                            },
                            // Cloudflare requires a catch-all rule at the end —
                            // anything that doesn't match the hostname above
                            // returns a 404.
                            new Cloudflare.Inputs.ZeroTrustTunnelCloudflaredConfigConfigIngressArgs
                            {
                                Service = "http_status:404",
                            },
                        },
                    },
                }, cfOpts);

            _ = new Cloudflare.DnsRecord("grafana-dns", new Cloudflare.DnsRecordArgs
            {
                ZoneId = cfZoneId,
                Name = grafanaHostname,
                Type = "CNAME",
                Content = Output.Format($"{tunnel.Id}.cfargotunnel.com"),
                Ttl = 1,            // Cloudflare interprets 1 as Auto.
                Proxied = true,
            }, cfOpts);

            // Connector token — derived from the tunnel id + secret. Read it
            // server-side rather than recomputing the HMAC locally.
            var tunnelToken = Cloudflare.GetZeroTrustTunnelCloudflaredToken.Invoke(
                new Cloudflare.GetZeroTrustTunnelCloudflaredTokenInvokeArgs
                {
                    AccountId = cfAccountId,
                    TunnelId = tunnel.Id,
                }, cfInvokeOpts).Apply(r => r.Token);

            // --- Cloudflare Access policy for Grafana ---
            //
            // Without this, the tunnel reaches Grafana directly and Grafana itself
            // runs as anonymous Admin ([Monitoring.cs] auth.anonymous.enabled) — so
            // anyone who knows the hostname has admin access. The Access app puts
            // Cloudflare's identity gate in front of the tunnel; only the listed
            // emails can authenticate (via One-Time PIN sent to that address).
            //
            // Set the allowlist with, e.g.:
            //   pulumi config set --stack oci-prod --path "grafanaAccessEmails[0]" me@example.com
            // Without it, Pulumi logs a warning and the tunnel stays unprotected.
            var grafanaAccessEmails = config.GetObject<string[]>("grafanaAccessEmails");
            if (grafanaAccessEmails is { Length: > 0 })
            {
                // One-Time PIN — Cloudflare auto-creates this IdP when Access is
                // enabled on the account, and only one onetimepin connection can
                // exist. So instead of creating a new resource (which 409s), look
                // up the existing one and reference its ID below.
                var otpIdpId = Cloudflare.GetZeroTrustAccessIdentityProviders.Invoke(
                    new Cloudflare.GetZeroTrustAccessIdentityProvidersInvokeArgs
                    {
                        AccountId = cfAccountId,
                    }, cfInvokeOpts).Apply(r =>
                    {
                        var otp = r.Results.FirstOrDefault(p => p.Type == "onetimepin");
                        if (otp is null)
                            throw new InvalidOperationException(
                                "No One-Time PIN identity provider found in the Cloudflare " +
                                "account. Enable Zero Trust Access in the dashboard first — " +
                                "Cloudflare provisions the OTP IdP automatically on first enable.");
                        return otp.Id;
                    });

                var grafanaIncludes = grafanaAccessEmails
                    .Select(email => new Cloudflare.Inputs.ZeroTrustAccessApplicationPolicyIncludeArgs
                    {
                        Email = new Cloudflare.Inputs.ZeroTrustAccessApplicationPolicyIncludeEmailArgs
                        {
                            Email = email,
                        },
                    })
                    .ToList();

                _ = new Cloudflare.ZeroTrustAccessApplication("grafana-access",
                    new Cloudflare.ZeroTrustAccessApplicationArgs
                    {
                        AccountId = cfAccountId,
                        Name = "Grafana",
                        Domain = grafanaHostname,
                        Type = "self_hosted",
                        SessionDuration = "24h",
                        AllowedIdps = { otpIdpId },
                        Policies =
                        {
                            new Cloudflare.Inputs.ZeroTrustAccessApplicationPolicyArgs
                            {
                                Name = "Allow listed emails",
                                Decision = "allow",
                                Precedence = 1,
                                Includes = grafanaIncludes,
                            },
                        },
                    }, cfOpts);
            }
            else
            {
                Pulumi.Log.Warn(
                    "grafanaAccessEmails is unset — Cloudflare Access is NOT configured " +
                    "and the Grafana tunnel will be reachable by anyone who knows the hostname. " +
                    "Run: pulumi config set --stack oci-prod --path \"grafanaAccessEmails[0]\" <your-email>");
            }

            // In-cluster cloudflared agent.
            var cloudflaredNamespaceName = "cloudflared";
            var cloudflaredNs = new Namespace("cloudflared", new NamespaceArgs
            {
                Metadata = new ObjectMetaArgs { Name = cloudflaredNamespaceName },
            }, providerOpts);

            var cloudflaredSecret = new Secret("cloudflared-token", new SecretArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "cloudflared-token",
                    Namespace = cloudflaredNamespaceName,
                },
                StringData = { ["token"] = tunnelToken },
            }, new CustomResourceOptions { Provider = k8s, DependsOn = { cloudflaredNs } });

            _ = new K8sDeployment("cloudflared", new DeploymentArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "cloudflared",
                    Namespace = cloudflaredNamespaceName,
                },
                Spec = new DeploymentSpecArgs
                {
                    // Single replica — each cloudflared instance opens 4 connections
                    // to Cloudflare's edge by default, which is already redundant
                    // across edge data centers. A second replica only buys us
                    // pod-level redundancy (kubelet restart), which isn't worth the
                    // extra resource overhead on a one-node system pool.
                    Replicas = 1,
                    Selector = new LabelSelectorArgs
                    {
                        MatchLabels = { ["app"] = "cloudflared" },
                    },
                    Template = new PodTemplateSpecArgs
                    {
                        Metadata = new ObjectMetaArgs
                        {
                            Labels = { ["app"] = "cloudflared" },
                        },
                        Spec = new PodSpecArgs
                        {
                            NodeSelector = { ["workload"] = "system" },
                            Containers =
                            {
                                new ContainerArgs
                                {
                                    Name = "cloudflared",
                                    // OKE worker nodes run with short-name mode enforcing, so
                                    // the image reference must be fully qualified — short
                                    // names like "cloudflare/cloudflared" get rejected as
                                    // ambiguous instead of defaulting to docker.io.
                                    Image = "docker.io/cloudflare/cloudflared:latest",
                                    // cloudflared auto-reads TUNNEL_TOKEN from the env var —
                                    // no need to pass it on the command line. --metrics exposes
                                    // an HTTP endpoint on :2000 that backs the /ready liveness
                                    // probe so kubelet can restart a stuck tunnel.
                                    Args =
                                    {
                                        "tunnel",
                                        "--no-autoupdate",
                                        "--loglevel", "info",
                                        "--metrics", "0.0.0.0:2000",
                                        "run",
                                    },
                                    Env =
                                    {
                                        new EnvVarArgs
                                        {
                                            Name = "TUNNEL_TOKEN",
                                            ValueFrom = new EnvVarSourceArgs
                                            {
                                                SecretKeyRef = new SecretKeySelectorArgs
                                                {
                                                    Name = "cloudflared-token",
                                                    Key = "token",
                                                },
                                            },
                                        },
                                    },
                                    Ports =
                                    {
                                        new ContainerPortArgs
                                        {
                                            Name = "metrics",
                                            ContainerPortValue = 2000,
                                        },
                                    },
                                    LivenessProbe = new ProbeArgs
                                    {
                                        HttpGet = new HTTPGetActionArgs
                                        {
                                            Path = "/ready",
                                            Port = 2000,
                                        },
                                        InitialDelaySeconds = 10,
                                        PeriodSeconds = 10,
                                        FailureThreshold = 1,
                                    },
                                    Resources = new ResourceRequirementsArgs
                                    {
                                        Requests =
                                        {
                                            ["cpu"] = "50m",
                                            ["memory"] = "64Mi",
                                        },
                                        Limits =
                                        {
                                            ["memory"] = "128Mi",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions { Provider = k8s, DependsOn = { cloudflaredSecret } });
        }
    }
    else
    {
        // --- Grafana HTTPRoute (local only) ---
        //
        // On local, expose Grafana through the same `main` Envoy Gateway that
        // serves the lobbies. No cloudflared, no auth — LAN trust is the
        // boundary.
        var grafanaHostname = config.Get("grafanaHostname");
        if (grafanaHostname is not null)
        {
            var grafanaRouteYaml = $@"
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: grafana
  namespace: {monitoringNamespaceName}
spec:
  parentRefs:
    - name: main
      namespace: {envoyGateway.Namespace}
  hostnames:
    - {grafanaHostname}
  rules:
    - matches:
        - path:
            type: PathPrefix
            value: /
      backendRefs:
        - name: monitoring-grafana
          port: 80
";

            _ = new ConfigGroup("grafana-route", new ConfigGroupArgs
            {
                Yaml = grafanaRouteYaml,
            }, new ComponentResourceOptions
            {
                Provider = k8s,
                DependsOn = { monitoring.Release, envoyGateway.Gateway },
            });
        }
    }

    // --- H-4 bastion tunnel helpers (local functions, hoisted) ---

    static string BuildTunnelCreateScript(string keyPath, string sessionId, string bastionHost, string apiIp)
    {
        // PowerShell single-quotes do not interpolate; escape any embedded single quotes
        // in the path by doubling. Session ID, bastion host, and API IP are
        // OCIDs / fixed FQDNs / IPs — no metacharacters to escape.
        var escapedKey = keyPath.Replace("'", "''");
        return $@"
$ErrorActionPreference = 'Stop'
$keyPath = '{escapedKey}'
$sessionId = '{sessionId}'
$bastionHost = '{bastionHost}'
$apiIp = '{apiIp}'
$pidFile = '.tunnel.pid'

function Test-TunnelUp {{
    return (Test-NetConnection -ComputerName 127.0.0.1 -Port 6443 -InformationLevel Quiet -WarningAction SilentlyContinue)
}}

# Idempotent: if the local forward port is already listening and our .tunnel.pid
# still points at a live process, an earlier `pulumi up` (or the helper script)
# left a working tunnel — leave it alone.
if ((Test-TunnelUp) -and (Test-Path $pidFile)) {{
    $existingPid = Get-Content $pidFile
    if (Get-Process -Id $existingPid -ErrorAction SilentlyContinue) {{
        Write-Host 'oke-api-tunnel: already up on 127.0.0.1:6443'
        exit 0
    }}
}}

# Stale PID file from a crashed run — clean up before relaunching.
if (Test-Path $pidFile) {{
    $stalePid = Get-Content $pidFile
    Stop-Process -Id $stalePid -Force -ErrorAction SilentlyContinue
    Remove-Item $pidFile -ErrorAction SilentlyContinue
}}

$sshArgs = @(
    '-i', $keyPath,
    '-N',
    '-L', ('6443:{{0}}:6443' -f $apiIp),
    '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ServerAliveInterval=30',
    '-p', '22',
    ('{{0}}@{{1}}' -f $sessionId, $bastionHost)
)
$proc = Start-Process -FilePath ssh -ArgumentList $sshArgs -PassThru -WindowStyle Hidden
$proc.Id | Out-File -Encoding ascii $pidFile

for ($i = 0; $i -lt 30; $i++) {{
    if (Test-TunnelUp) {{
        Write-Host 'oke-api-tunnel: up on 127.0.0.1:6443'
        exit 0
    }}
    Start-Sleep -Seconds 1
}}

# Fail loud so Pulumi reports the error rather than racing downstream K8s reconciles.
Write-Error 'oke-api-tunnel: local forward did not come up within 30s on 127.0.0.1:6443'
exit 1
".Replace("\r\n", "\n");
    }

    static string BuildTunnelDeleteScript()
    {
        return @"
if (Test-Path .tunnel.pid) {
    Stop-Process -Id (Get-Content .tunnel.pid) -Force -ErrorAction SilentlyContinue
    Remove-Item .tunnel.pid -ErrorAction SilentlyContinue
}
".Replace("\r\n", "\n");
    }

    static string PatchKubeconfigForPortForward(string yaml)
    {
        // OKE's kubeconfig has one `server: https://<host>:6443` line per cluster
        // entry. Rewrite it to point at the local ssh -L forward (127.0.0.1:6443)
        // and add a sibling `tls-server-name: <original-host>` so client-go's TLS
        // verification still matches the API server cert's SANs (which include
        // both the FQDN and the private IP, but not 127.0.0.1).
        var normalized = yaml.Replace("\r\n", "\n");
        var matched = false;
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^(?<indent>[ \t]+)server:[ \t]+https://(?<host>[^\s:/]+)(:(?<port>\d+))?[ \t]*\n",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var patched = pattern.Replace(normalized, m =>
        {
            matched = true;
            var indent = m.Groups["indent"].Value;
            var host = m.Groups["host"].Value;
            return $"{indent}server: https://127.0.0.1:6443\n" +
                   $"{indent}tls-server-name: {host}\n";
        });
        if (!matched)
        {
            throw new InvalidOperationException(
                "Failed to rewrite OKE kubeconfig — no `server: https://...` line found. " +
                "The kubeconfig format may have changed.");
        }
        return patched;
    }

    return new Dictionary<string, object?>
    {
        ["namespace"] = envoyGateway.Namespace,
        ["envoyGatewayVersion"] = envoyGateway.Version,
        ["agonesNamespace"] = agones.Namespace,
        ["agonesVersion"] = agones.Version,
        ["monitoringNamespace"] = monitoringNamespaceName,
        ["kubePrometheusStackVersion"] = monitoring.Version,
        ["registryEnabled"] = isLocal,
        ["registryHostname"] = isLocal ? (object?)registryHostname : null,
        ["clusterId"] = oci?.ClusterId,
        ["lobbiesRepositoryId"] = oci?.LobbiesRepositoryId,
        ["gameRepositoryId"] = oci?.GameRepositoryId,
        // Surfaced for Open-BastionTunnel.ps1 — the helper reads these via
        // `pulumi stack output` to reopen the SOCKS5 tunnel between `pulumi up`s.
        ["bastionSessionId"] = oci?.BastionSessionId,
        ["bastionHost"] = oci?.BastionHost,
        ["apiEndpointIp"] = oci?.ApiEndpointIp,
        // The kubeconfig already includes the SOCKS5 proxy-url injection — the
        // operator can dump it to disk and point KUBECONFIG at it. Marked as a
        // secret because it carries the cluster CA bundle and exec credentials.
        ["kubeConfig"] = oci is null ? null : Output.CreateSecret(
            oci.KubeConfig.Apply(PatchKubeconfigForPortForward)),
        ["postgresEnabled"] = deployPostgres,
        ["postgresNamespace"] = postgres?.DatabaseNamespace,
        ["postgresPoolerHost"] = postgres?.PoolerHost,
        ["postgresPoolerPort"] = (object?)postgres?.PoolerPort,
    };
});
