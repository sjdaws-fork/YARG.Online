using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V4;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V4;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Yaml;
using YARG.Online.Infrastructure;
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
    var controller = new HelmRelease("envoy-gateway", new HelmReleaseArgs
    {
        Chart = "oci://docker.io/envoyproxy/gateway-helm",
        Version = envoyGatewayVersion,
        Namespace = ns.Metadata.Apply(m => m.Name!),
        SkipCrds = true,
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
                ["controller"] = new Dictionary<string, object>
                {
                    ["replicas"] = 1,
                },
                ["allocator"] = new Dictionary<string, object>
                {
                    ["replicas"] = 1,
                    ["service"] = new Dictionary<string, object>
                    {
                        ["serviceType"] = "ClusterIP",
                    },
                },
                ["ping"] = new Dictionary<string, object>
                {
                    ["install"] = false,
                },
            },
        },
    }, new CustomResourceOptions
    {
        Provider = k8s,
        DependsOn = { agonesNs },
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

    return new Dictionary<string, object?>
    {
        ["namespace"] = ns.Metadata.Apply(m => m.Name!),
        ["envoyGatewayVersion"] = envoyGatewayVersion,
        ["agonesNamespace"] = agonesNs.Metadata.Apply(m => m.Name!),
        ["agonesVersion"] = agonesVersion,
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
