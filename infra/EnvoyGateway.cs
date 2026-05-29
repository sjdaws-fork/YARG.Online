using System;
using System.Collections.Generic;
using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V4;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V4;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Yaml;
using ApiExtCustomResource = Pulumi.Kubernetes.ApiExtensions.CustomResource;
using ApiExtCustomResourceArgs = Pulumi.Kubernetes.ApiExtensions.CustomResourceArgs;
using Cloudflare = Pulumi.Cloudflare;
using HelmRelease = Pulumi.Kubernetes.Helm.V3.Release;
using HelmReleaseArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.ReleaseArgs;
using Tls = Pulumi.Tls;

namespace YARG.Online.Infrastructure;

/// <summary>
/// Handles for the Envoy Gateway install: namespace, chart version (exposed as a
/// stack output), the CRD chart and controller release (downstream
/// <c>DependsOn</c> wiring for external-dns), and the in-cluster Gateway used by
/// HTTPRoute parents.
/// </summary>
public sealed record EnvoyGatewayResources(
    string Namespace,
    string Version,
    Chart Crds,
    HelmRelease Controller,
    ConfigGroup Gateway);

/// <summary>
/// Installs Envoy Gateway (CRDs + controller), the <c>envoy</c> GatewayClass,
/// the <c>main</c> Gateway, and the Prometheus Operator monitors that scrape
/// the control plane and data plane. On OCI stacks also installs the
/// <c>oci-nlb</c> EnvoyProxy CR that pins the Gateway to an OCI Network Load
/// Balancer with the cluster's reserved IP / subnet / NSG.
///
/// Controller chart values are loaded from
/// <c>infra/values/common/envoy-gateway.yaml</c> + <c>infra/values/{stack}/envoy-gateway.yaml</c>,
/// matching the pattern in <see cref="Monitoring.Deploy"/>.
/// </summary>
public static class EnvoyGateway
{
    private const string NamespaceName = "envoy-gateway-system";
    private const string GatewayName = "main";
    private const string GatewayClassName = "envoy";
    private const string OciNlbProxyName = "oci-nlb";

    public static EnvoyGatewayResources Deploy(
        Pulumi.Config config,
        Provider k8s,
        OracleResources? oci,
        string monitoringNamespace,
        Resource monitoringDependency,
        Cloudflare.Provider? cfProvider,
        string? cfApexDomain,
        CustomResourceOptions providerOpts)
    {
        var version = config.Require("envoyGatewayVersion");
        var gatewayApiChannel = config.Require("gatewayApiChannel");

        var (commonValuesPath, stackValuesPath) = StackValues.ResolveCommonAndStack(
            "envoy-gateway.yaml", "Envoy Gateway", "EnvoyGateway.Deploy");

        var ns = new Namespace("envoy-gateway-system", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = NamespaceName },
        }, providerOpts);

        var crds = new Chart("envoy-gateway-crds", new ChartArgs
        {
            Chart = "oci://docker.io/envoyproxy/gateway-crds-helm",
            Version = version,
            Namespace = NamespaceName,
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
        //
        // ValueYamlFiles list order matters: later files override earlier on
        // overlapping keys, so the stack file wins over common.
        var controller = new HelmRelease("envoy-gateway", new HelmReleaseArgs
        {
            Chart = "oci://docker.io/envoyproxy/gateway-helm",
            Version = version,
            Namespace = NamespaceName,
            SkipCrds = true,
            ValueYamlFiles = new InputList<AssetOrArchive>
            {
                new FileAsset(commonValuesPath),
                new FileAsset(stackValuesPath),
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            DependsOn = { crds },
        });

        // On OCI, an EnvoyProxy config tells the OKE cloud-controller to expose the Gateway
        // through an OCI Network Load Balancer (L4 pass-through) instead of the classic LB.
        // `is-preserve-source` keeps the client IP and needs externalTrafficPolicy: Local.
        ApiExtCustomResource? envoyProxy = null;
        if (oci is not null)
        {
            envoyProxy = new ApiExtCustomResource("oci-nlb-envoyproxy", new EnvoyProxyArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = OciNlbProxyName,
                    Namespace = NamespaceName,
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
    name: {OciNlbProxyName}
    namespace: {NamespaceName}"
            : "";

        var gatewayClassYaml = $@"
apiVersion: gateway.networking.k8s.io/v1
kind: GatewayClass
metadata:
  name: {GatewayClassName}
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

        // --- TLS at the edge (oci-prod with Cloudflare only) ---
        //
        // Issue a Cloudflare Origin CA certificate at `pulumi up` time and bind
        // it to a kubernetes.io/tls Secret in this namespace. The Gateway HTTPS
        // listener below references it via certificateRefs. SANs cover the apex
        // and a wildcard so new public subdomains don't require re-issuing.
        //
        // Cert is only accepted by Cloudflare's edge — useless to a client
        // bypassing Cloudflare. Pair with Cloudflare's "Full (strict)" zone
        // setting (flipped manually in the dashboard after this deploys).
        Secret? tlsSecret = null;
        if (oci is not null && cfProvider is not null && !string.IsNullOrWhiteSpace(cfApexDomain))
        {
            var key = new Tls.PrivateKey("cf-origin-key", new Tls.PrivateKeyArgs
            {
                Algorithm = "RSA",
                RsaBits = 2048,
            });

            var csr = new Tls.CertRequest("cf-origin-csr", new Tls.CertRequestArgs
            {
                PrivateKeyPem = key.PrivateKeyPem,
                Subject = new Tls.Inputs.CertRequestSubjectArgs
                {
                    CommonName = cfApexDomain,
                },
                DnsNames = { cfApexDomain, $"*.{cfApexDomain}" },
            });

            var originCert = new Cloudflare.OriginCaCertificate("cf-origin-cert",
                new Cloudflare.OriginCaCertificateArgs
                {
                    Csr = csr.CertRequestPem,
                    Hostnames = { cfApexDomain, $"*.{cfApexDomain}" },
                    RequestType = "origin-rsa",
                    RequestedValidity = 5475, // 15 years (Cloudflare max)
                }, new CustomResourceOptions { Provider = cfProvider });

            tlsSecret = new Secret("gateway-tls", new SecretArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "gateway-tls",
                    Namespace = NamespaceName,
                },
                Type = "kubernetes.io/tls",
                StringData =
                {
                    ["tls.crt"] = originCert.Certificate,
                    ["tls.key"] = key.PrivateKeyPem,
                },
            }, new CustomResourceOptions { Provider = k8s, DependsOn = { ns } });
        }
        else if (oci is not null)
        {
            throw new InvalidOperationException(
                "OCI stack requires Cloudflare TLS for the public Gateway. " +
                "Set `cloudflareApiToken` (with Zone.SSL-and-Certificates:Edit) and " +
                "`cloudflareApexDomain` (e.g. 'yarg.online') before `pulumi up`.");
        }

        // external-dns derives a record's target from the Gateway's address. On OCI the
        // cloud-controller reports the NLB's *private* in-subnet IP there, which Cloudflare
        // rejects for a proxied record — so pin the target to the NLB's reserved *public*
        // IP via the external-dns target annotation (read on the Gateway, not on Routes).
        var gatewayAnnotationsYaml = oci is not null
            ? Output.Format($@"
  annotations:
    external-dns.alpha.kubernetes.io/target: {oci.NlbReservedIp}")
            : Output.Create("");

        // OCI public path: HTTPS on :443 only. Cloudflare ↔ origin uses the
        // Origin CA cert above. The :80 listener is intentionally absent — the
        // NSG is also locked to Cloudflare's proxy IPs on :443 only.
        //
        // Local/dev path: plain HTTP on :80 (no NLB, no Cloudflare, cluster-local
        // ingress on the host's loopback / kind port mapping).
        var listenersYaml = oci is not null
            ? $@"
    - name: https
      port: 443
      protocol: HTTPS
      tls:
        mode: Terminate
        certificateRefs:
          - kind: Secret
            name: gateway-tls
            namespace: {NamespaceName}
      allowedRoutes:
        namespaces:
          from: All"
            : $@"
    - name: http
      port: 80
      protocol: HTTP
      allowedRoutes:
        namespaces:
          from: All";

        var gatewayYaml = Output.Format($@"
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: {GatewayName}
  namespace: {NamespaceName}{gatewayAnnotationsYaml}
spec:
  gatewayClassName: {GatewayClassName}
  listeners:{listenersYaml}
");

        var gatewayOpts = new ComponentResourceOptions
        {
            Provider = k8s,
            DependsOn = { controller, gatewayClass },
        };
        if (tlsSecret is not null)
            gatewayOpts.DependsOn.Add(tlsSecret);

        var gateway = new ConfigGroup("main-gateway", new ConfigGroupArgs
        {
            Yaml = gatewayYaml,
        }, gatewayOpts);

        // Envoy Gateway's upstream chart only emits prometheus.io/scrape annotations on its
        // pods — kube-prometheus-stack doesn't honor those. Bridge with proper
        // ServiceMonitor / PodMonitor resources.
        //
        // Metrics endpoint reference (Envoy Gateway docs):
        // • Control plane: port 19001, path /metrics
        // • Data plane:    port 19001, path /stats/prometheus
        //
        // The `release: monitoring` label is what kube-prometheus-stack's default
        // selectors look for — it must match Monitoring.ReleaseName.
        var monitorsYaml = $@"
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: envoy-gateway-controller
  namespace: {monitoringNamespace}
  labels:
    release: monitoring
spec:
  namespaceSelector:
    matchNames: [{NamespaceName}]
  selector:
    matchLabels:
      control-plane: envoy-gateway
  endpoints:
    - port: metrics
      path: /metrics
      interval: 30s
---
apiVersion: monitoring.coreos.com/v1
kind: PodMonitor
metadata:
  name: envoy-gateway-proxy
  namespace: {monitoringNamespace}
  labels:
    release: monitoring
spec:
  namespaceSelector:
    matchNames: [{NamespaceName}]
  selector:
    matchLabels:
      app.kubernetes.io/component: proxy
      app.kubernetes.io/managed-by: envoy-gateway
  podMetricsEndpoints:
    - port: metrics
      path: /stats/prometheus
      interval: 30s
";

        _ = new ConfigGroup("envoy-gateway-monitors", new ConfigGroupArgs
        {
            Yaml = monitorsYaml,
        }, new ComponentResourceOptions
        {
            Provider = k8s,
            DependsOn = { controller, monitoringDependency },
        });

        return new EnvoyGatewayResources(
            Namespace: NamespaceName,
            Version: version,
            Crds: crds,
            Controller: controller,
            Gateway: gateway);
    }

    /// <summary>Args for the Envoy Gateway <c>EnvoyProxy</c> CRD instance (no typed SDK).</summary>
    private sealed class EnvoyProxyArgs : ApiExtCustomResourceArgs
    {
        [Input("spec")]
        public Input<object>? Spec { get; set; }

        public EnvoyProxyArgs() : base("gateway.envoyproxy.io/v1alpha1", "EnvoyProxy") { }
    }
}
