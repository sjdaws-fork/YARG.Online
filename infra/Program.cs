using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V4;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V4;
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
    var envoyGatewayVersion = config.Require("envoyGatewayVersion");
    var gatewayApiChannel = config.Require("gatewayApiChannel");
    var agonesVersion = config.Get("agonesVersion") ?? "1.57.0";
    var namespacePrefix = config.Get("namespacePrefix") ?? "";

    string PrefixedNs(string name) =>
        string.IsNullOrEmpty(namespacePrefix) ? name : $"{namespacePrefix}-{name}";

    // The `local` stack attaches to an existing kube context; OCI stacks provision an
    // OKE cluster first and drive the Kubernetes provider from its generated kubeconfig.
    OracleResources? oci = null;
    Provider k8s;
    if (provisionOke)
    {
        oci = Oracle.Provision(config);
        k8s = new Provider("oci", new ProviderArgs { KubeConfig = oci.KubeConfig });
    }
    else
    {
        k8s = new Provider("local", new ProviderArgs { Context = config.Require("kubeContext") });
    }

    var providerOpts = new CustomResourceOptions { Provider = k8s };

    // Pin control-plane workloads to the `system` node pool on OCI. Local k3s has no
    // such label, so leave the selector null there — matches MonitoringProfile.Light,
    // which skips its nodeSelector to avoid Pending pods on a single-node cluster.
    var systemNodeSelector = provisionOke
        ? new Dictionary<string, object> { ["workload"] = "system" }
        : null;

    // --- kube-prometheus-stack ---
    //
    // Installed first so its Prometheus Operator CRDs (ServiceMonitor, PodMonitor,
    // etc.) exist before any other chart tries to create those resources. Agones,
    // for example, ships its own ServiceMonitor when metrics.serviceMonitor.enabled
    // is true and would fail with "resource mapping not found" on a fresh cluster
    // if it raced the monitoring install.
    //
    // Local k3s uses the `local-path` StorageClass and the small Light profile;
    // oci-prod uses `oci-bv` and the Full profile pinned to the workload=system
    // node pool. Resource shapes and PVC sizes live in MonitoringProfile
    // (Monitoring.cs), not Pulumi config.
    var monitoringNamespaceName = PrefixedNs("monitoring");
    var monitoringNs = new Namespace("monitoring", new NamespaceArgs
    {
        Metadata = new ObjectMetaArgs { Name = monitoringNamespaceName },
    }, providerOpts);

    var monitoringProfile = provisionOke ? MonitoringProfile.Full : MonitoringProfile.Light;
    var monitoringStorageClass = config.Get("monitoringStorageClass")
        ?? (provisionOke ? "oci-bv" : "local-path");
    var kubePrometheusStackVersion = config.Get("kubePrometheusStackVersion") ?? "85.2.2";

    var monitoring = new HelmRelease("kube-prometheus-stack", new HelmReleaseArgs
    {
        // The release name is referenced by the lobbies/game ServiceMonitor/PodMonitor
        // selectors (release=monitoring) and by the in-cluster Service DNS names
        // (monitoring-grafana, monitoring-kube-prometheus-prometheus, …). Don't
        // rename without updating those callers.
        Name = "monitoring",
        Chart = "kube-prometheus-stack",
        Version = kubePrometheusStackVersion,
        RepositoryOpts = new V3RepositoryOptsArgs
        {
            Repo = "https://prometheus-community.github.io/helm-charts",
        },
        Namespace = monitoringNamespaceName,
        Values = Monitoring.BuildValues(monitoringProfile, monitoringStorageClass),
    }, new CustomResourceOptions
    {
        Provider = k8s,
        DependsOn = { monitoringNs },
    });

    var envoyGatewayNamespaceName = PrefixedNs("envoy-gateway-system");

    var ns = new Namespace("envoy-gateway-system", new NamespaceArgs
    {
        Metadata = new ObjectMetaArgs
        {
            Name = envoyGatewayNamespaceName,
        },
    }, providerOpts);

    var crds = new Chart("envoy-gateway-crds", new ChartArgs
    {
        Chart = "oci://docker.io/envoyproxy/gateway-crds-helm",
        Version = envoyGatewayVersion,
        Namespace = ns.Metadata.Apply(m => m.Name!),
        Values = new InputMap<object>
        {
            ["crds"] = new Dictionary<string, object>
            {
                ["gatewayAPI"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["channel"] = gatewayApiChannel,
                },
                ["envoyGateway"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                },
            },
        },
    }, new ComponentResourceOptions
    {
        Provider = k8s,
        DependsOn = { ns },
    });

    // The Envoy Gateway controller chart ships its TLS bootstrap as a Helm pre-install
    // hook (envoy-gateway-certgen Job -> envoy-gateway Secret). helm.v4.Chart renders
    // like `helm template` and drops hooks, so the cert Secret never gets created and
    // the controller Pod hangs on a missing volume. Release runs hooks via the
    // embedded Helm SDK, so use it for anything that depends on install-time hooks.
    // For OCI registries the full `oci://...` URL goes in `Chart` directly. Using
    // `RepositoryOpts.Repo` triggers a `helm repo add` flow that isn't valid for OCI
    // (OCI bundles registry + chart in one reference).
    var envoyGatewayValues = new Dictionary<string, object>();
    if (systemNodeSelector is not null)
    {
        envoyGatewayValues["deployment"] = new Dictionary<string, object>
        {
            ["pod"] = new Dictionary<string, object>
            {
                ["nodeSelector"] = systemNodeSelector,
            },
        };
        // certgen is a Helm pre-install Job that creates the controller's TLS
        // bootstrap Secret. Pin it too so it doesn't transiently consume capacity
        // on a workload node pool during install.
        envoyGatewayValues["certgen"] = new Dictionary<string, object>
        {
            ["job"] = new Dictionary<string, object>
            {
                ["nodeSelector"] = systemNodeSelector,
            },
        };
    }

    var controller = new HelmRelease("envoy-gateway", new HelmReleaseArgs
    {
        Chart = "oci://docker.io/envoyproxy/gateway-helm",
        Version = envoyGatewayVersion,
        Namespace = ns.Metadata.Apply(m => m.Name!),
        SkipCrds = true,
        Values = envoyGatewayValues,
    }, new CustomResourceOptions
    {
        Provider = k8s,
        DependsOn = { crds },
    });

    var agonesNamespaceName = PrefixedNs("agones-system");

    var agonesNs = new Namespace("agones-system", new NamespaceArgs
    {
        Metadata = new ObjectMetaArgs
        {
            Name = agonesNamespaceName,
        },
    }, providerOpts);

    // CRDs are bundled in this chart's templates (not under `crds/`), so a single
    // Release covers both. Single-replica controller/allocator keeps the footprint
    // small. ClusterIP for the allocator since the lobby calls Agones in-cluster via
    // the K8s API. Ping is disabled — clients don't query Agones in our architecture.
    var agonesController = new Dictionary<string, object> { ["replicas"] = 1 };
    var agonesAllocator = new Dictionary<string, object>
    {
        ["replicas"] = 1,
        ["service"] = new Dictionary<string, object>
        {
            ["serviceType"] = "ClusterIP",
        },
    };
    var agonesExtensions = new Dictionary<string, object>();
    if (systemNodeSelector is not null)
    {
        agonesController["nodeSelector"] = systemNodeSelector;
        agonesAllocator["nodeSelector"] = systemNodeSelector;
        agonesExtensions["nodeSelector"] = systemNodeSelector;
    }

    var agones = new HelmRelease("agones", new HelmReleaseArgs
    {
        Name = "agones",
        Chart = "agones",
        Version = agonesVersion,
        RepositoryOpts = new V3RepositoryOptsArgs
        {
            Repo = "https://agones.dev/chart/stable",
        },
        Namespace = agonesNs.Metadata.Apply(m => m.Name!),
        Values = new InputMap<object>
        {
            ["agones"] = new Dictionary<string, object>
            {
                ["controller"] = agonesController,
                ["allocator"] = agonesAllocator,
                ["extensions"] = agonesExtensions,
                ["ping"] = new Dictionary<string, object>
                {
                    ["install"] = false,
                },
                // The Agones chart ships its own ServiceMonitor (covers
                // controller, allocator, extensions). Toggle it on so the
                // kube-prometheus-stack release scrapes Agones automatically —
                // no extra resources to maintain on our side.
                ["metrics"] = new Dictionary<string, object>
                {
                    ["serviceMonitor"] = new Dictionary<string, object>
                    {
                        ["enabled"] = true,
                        ["interval"] = "30s",
                    },
                },
            },
        },
    }, new CustomResourceOptions
    {
        Provider = k8s,
        // monitoring must be in place before Agones — the chart's metrics
        // ServiceMonitor depends on the Prometheus Operator CRDs that
        // kube-prometheus-stack installs.
        DependsOn = { agonesNs, monitoring },
    });

    // On OCI, an EnvoyProxy config tells the OKE cloud-controller to expose the Gateway
    // through an OCI Network Load Balancer (L4 pass-through) instead of the classic LB.
    // `is-preserve-source` keeps the client IP and needs externalTrafficPolicy: Local.
    Pulumi.Kubernetes.ApiExtensions.CustomResource? envoyProxy = null;
    if (oci is not null)
    {
        envoyProxy = new Pulumi.Kubernetes.ApiExtensions.CustomResource("oci-nlb-envoyproxy", new EnvoyProxyArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Name = "oci-nlb",
                Namespace = envoyGatewayNamespaceName,
            },
            Spec = new Dictionary<string, object>
            {
                ["provider"] = new Dictionary<string, object>
                {
                    ["type"] = "Kubernetes",
                    ["kubernetes"] = new Dictionary<string, object>
                    {
                        // Pin the data-plane Envoy pods to the system pool. With
                        // externalTrafficPolicy: Local the NLB will only forward to
                        // nodes running an Envoy pod, so this also concentrates
                        // ingress on `system` — fine while Envoy is single-replica.
                        ["envoyDeployment"] = new Dictionary<string, object>
                        {
                            ["pod"] = new Dictionary<string, object>
                            {
                                ["nodeSelector"] = new Dictionary<string, object>
                                {
                                    ["workload"] = "system",
                                },
                            },
                        },
                        ["envoyService"] = new Dictionary<string, object>
                        {
                            ["type"] = "LoadBalancer",
                            ["externalTrafficPolicy"] = "Local",
                            ["annotations"] = new Dictionary<string, object>
                            {
                                ["oci.oraclecloud.com/load-balancer-type"] = "nlb",
                                ["oci.oraclecloud.com/reserved-ips"] = oci.NlbReservedIp,
                                ["oci-network-load-balancer.oraclecloud.com/subnet"] = oci.NlbSubnetId,
                                ["oci-network-load-balancer.oraclecloud.com/oci-network-security-groups"] = oci.NlbNsgId,
                                ["oci-network-load-balancer.oraclecloud.com/is-preserve-source"] = "false",
                            },
                        },
                    },
                },
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            DependsOn = { crds },
        });
    }

    // Envoy Gateway v1.x doesn't ship a default GatewayClass — declare one explicitly.
    // On OCI it references the EnvoyProxy above so the Gateway inherits the NLB config.
    var parametersRefYaml = oci is not null
        ? $@"
  parametersRef:
    group: gateway.envoyproxy.io
    kind: EnvoyProxy
    name: oci-nlb
    namespace: {envoyGatewayNamespaceName}"
        : "";

    var gatewayClassYaml = $@"
apiVersion: gateway.networking.k8s.io/v1
kind: GatewayClass
metadata:
  name: envoy
spec:
  controllerName: gateway.envoyproxy.io/gatewayclass-controller{parametersRefYaml}
";

    var gatewayClassOpts = new ComponentResourceOptions
    {
        Provider = k8s,
        DependsOn = { controller },
    };
    if (envoyProxy is not null)
        gatewayClassOpts.DependsOn.Add(envoyProxy);

    var gatewayClass = new ConfigGroup("envoy-gateway-class", new ConfigGroupArgs
    {
        Yaml = gatewayClassYaml,
    }, gatewayClassOpts);

    // external-dns derives a record's target from the Gateway's address. On OCI the
    // cloud-controller reports the NLB's *private* in-subnet IP there, which Cloudflare
    // rejects for a proxied record — so pin the target to the NLB's reserved *public*
    // IP via the external-dns target annotation (read on the Gateway, not on Routes).
    var gatewayAnnotationsYaml = oci is not null
        ? Output.Format($@"
  annotations:
    external-dns.alpha.kubernetes.io/target: {oci.NlbReservedIp}")
        : Output.Create("");

    var gatewayYaml = Output.Format($@"
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: main
  namespace: {envoyGatewayNamespaceName}{gatewayAnnotationsYaml}
spec:
  gatewayClassName: envoy
  listeners:
    - name: http
      port: 80
      protocol: HTTP
      allowedRoutes:
        namespaces:
          from: All
");

    var gateway = new ConfigGroup("main-gateway", new ConfigGroupArgs
    {
        Yaml = gatewayYaml,
    }, new ComponentResourceOptions
    {
        Provider = k8s,
        DependsOn = { controller, gatewayClass },
    });

    var isLocal = Deployment.Instance.StackName == "local";

    string? registryHostname = null;

    if (isLocal)
    {
        registryHostname = config.Require("registryHostname");
        var registryUsername = config.Require("registryUsername");
        var registryPassword = config.RequireSecret("registryPassword");
        var registryStorageSize = config.Get("registryStorageSize") ?? "20Gi";

        var registryNamespaceName = PrefixedNs("registry");

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
      namespace: {envoyGatewayNamespaceName}
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
            DependsOn = { registry, gateway },
        });
    }

    // Cloudflare front-door: external-dns syncs Cloudflare DNS records straight from
    // the Gateway API HTTPRoutes — hostnames live only in the app charts, and the
    // record targets the Gateway's address (the NLB's reserved IP). It only deploys
    // once the Cloudflare API token and zone domain are configured.
    if (provisionOke)
    {
        var cfToken = config.GetSecret("cloudflareApiToken");
        var cfDomain = config.Get("cloudflareDomainFilter");

        if (cfToken is not null && cfDomain is not null)
        {
            var externalDnsNamespaceName = PrefixedNs("external-dns");

            var externalDnsNs = new Namespace("external-dns", new NamespaceArgs
            {
                Metadata = new ObjectMetaArgs { Name = externalDnsNamespaceName },
            }, providerOpts);

            var cloudflareSecret = new Secret("external-dns-cloudflare", new SecretArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "external-dns-cloudflare",
                    Namespace = externalDnsNamespaceName,
                },
                StringData = { ["cloudflare_api_token"] = cfToken },
            }, new CustomResourceOptions { Provider = k8s, DependsOn = { externalDnsNs } });

            // Tight resource caps — external-dns is a small controller that watches a
            // handful of routes and reconciles on an interval; it barely registers.
            var externalDns = new HelmRelease("external-dns", new HelmReleaseArgs
            {
                Name = "external-dns",
                Chart = "external-dns",
                RepositoryOpts = new V3RepositoryOptsArgs
                {
                    Repo = "https://kubernetes-sigs.github.io/external-dns/",
                },
                Namespace = externalDnsNamespaceName,
                Values = new InputMap<object>
                {
                    ["provider"] = new Dictionary<string, object> { ["name"] = "cloudflare" },
                    ["sources"] = new[] { "gateway-httproute" },
                    ["domainFilters"] = new[] { cfDomain },
                    ["policy"] = "sync",
                    ["txtOwnerId"] = "yarg-online-oci",
                    ["extraArgs"] = new[] { "--cloudflare-proxied" },
                    ["env"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "CF_API_TOKEN",
                            ["valueFrom"] = new Dictionary<string, object>
                            {
                                ["secretKeyRef"] = new Dictionary<string, object>
                                {
                                    ["name"] = "external-dns-cloudflare",
                                    ["key"] = "cloudflare_api_token",
                                },
                            },
                        },
                    },
                    ["resources"] = new Dictionary<string, object>
                    {
                        ["requests"] = new Dictionary<string, object>
                        {
                            ["cpu"] = "10m",
                            ["memory"] = "64Mi",
                        },
                        ["limits"] = new Dictionary<string, object>
                        {
                            ["memory"] = "128Mi",
                        },
                    },
                    ["nodeSelector"] = new Dictionary<string, object>
                    {
                        ["workload"] = "system",
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = k8s,
                DependsOn = { crds, cloudflareSecret },
            });
        }
    }

    // Dashboards (downloaded by Download-Dashboards.ps1) become labeled
    // ConfigMaps in the monitoring namespace; the Grafana sidecar picks them
    // up automatically. If the directory is empty (a developer hasn't run the
    // download script), Grafana still has its chart-bundled dashboards.
    Monitoring.InstallDashboards(k8s, monitoringNamespaceName, monitoring);

    // Envoy Gateway's upstream chart only emits prometheus.io/scrape
    // annotations on its pods — kube-prometheus-stack doesn't honor those.
    // Bridge with proper ServiceMonitor / PodMonitor resources.
    Monitoring.InstallEnvoyGatewayMonitors(
        k8s, envoyGatewayNamespaceName, monitoringNamespaceName, monitoring);

    if (provisionOke)
    {
        // --- Cloudflare Tunnel + cloudflared (oci-prod only) ---
        //
        // Outbound-only path into Grafana. Cloudflare Access (configured once
        // out-of-band in the Zero Trust dashboard) authenticates at the edge.
        // Reuses the existing cloudflareApiToken used for external-dns; that
        // token's scope must include Account.Cloudflare Tunnel:Edit and
        // Account Settings:Read in addition to its existing Zone.DNS:Edit.
        var cfToken = config.GetSecret("cloudflareApiToken");
        var cfAccountId = config.Get("cloudflareAccountId");
        var cfZoneId = config.Get("cloudflareZoneId");
        var grafanaHostname = config.Get("grafanaHostname");

        if (cfToken is not null && cfAccountId is not null && cfZoneId is not null
            && grafanaHostname is not null)
        {
            var cfProvider = new Cloudflare.Provider("cloudflare", new Cloudflare.ProviderArgs
            {
                ApiToken = cfToken,
            });
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
                    Name = string.IsNullOrEmpty(namespacePrefix)
                        ? "grafana"
                        : $"{namespacePrefix}-grafana",
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
            var cloudflaredNamespaceName = PrefixedNs("cloudflared");
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
      namespace: {envoyGatewayNamespaceName}
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
                DependsOn = { monitoring, gateway },
            });
        }
    }

    return new Dictionary<string, object?>
    {
        ["namespace"] = ns.Metadata.Apply(m => m.Name!),
        ["envoyGatewayVersion"] = envoyGatewayVersion,
        ["agonesNamespace"] = agonesNs.Metadata.Apply(m => m.Name!),
        ["agonesVersion"] = agonesVersion,
        ["monitoringNamespace"] = monitoringNamespaceName,
        ["kubePrometheusStackVersion"] = kubePrometheusStackVersion,
        ["registryEnabled"] = isLocal,
        ["registryHostname"] = isLocal ? (object?)registryHostname : null,
        ["clusterId"] = oci?.ClusterId,
        ["lobbiesRepositoryId"] = oci?.LobbiesRepositoryId,
        ["gameRepositoryId"] = oci?.GameRepositoryId,
    };
});

/// <summary>Args for the Envoy Gateway `EnvoyProxy` CRD instance (no typed SDK).</summary>
sealed class EnvoyProxyArgs : Pulumi.Kubernetes.ApiExtensions.CustomResourceArgs
{
    [Pulumi.Input("spec")]
    public Pulumi.Input<object>? Spec { get; set; }

    public EnvoyProxyArgs() : base("gateway.envoyproxy.io/v1alpha1", "EnvoyProxy") { }
}
