using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Yaml;

namespace YARG.Online.Infrastructure;

/// <summary>
/// Pod-level CPU/memory shape used by every component of the monitoring stack.
/// CPU limit is intentionally omitted — limit-based throttling on observability
/// pods is a footgun and the requests already give the scheduler everything it
/// needs.
/// </summary>
public sealed record ResourceShape(string CpuRequest, string MemoryRequest, string MemoryLimit);

/// <summary>
/// Sizing profile for the kube-prometheus-stack release. Two static instances:
/// <see cref="Light"/> for local k3s (small workstation footprint, no node
/// selector) and <see cref="Full"/> for OCI/OKE (production sizing, pinned to
/// the <c>workload=system</c> node pool). Lives in code, not Pulumi config —
/// these are sizing decisions, not deploy-time variables.
/// </summary>
public sealed record MonitoringProfile(
    string PrometheusPvcSize,
    string GrafanaPvcSize,
    string AlertmanagerPvcSize,
    ResourceShape Prometheus,
    ResourceShape Grafana,
    ResourceShape Alertmanager,
    ResourceShape KubeStateMetrics,
    ResourceShape NodeExporter,
    ResourceShape Operator,
    Dictionary<string, object>? NodeSelector)
{
    public static readonly MonitoringProfile Light = new(
        PrometheusPvcSize: "5Gi",
        GrafanaPvcSize: "2Gi",
        AlertmanagerPvcSize: "1Gi",
        Prometheus:       new("300m", "256Mi", "1Gi"),
        // Grafana 13's startup peaks well over 256Mi (apiserver init, ~50
        // plugin loads, ngalert + ngalert.notifier setup); idle settles to
        // ~150–200Mi. 128Mi OOMKills during boot.
        Grafana:          new("50m",  "128Mi", "512Mi"),
        Alertmanager:     new("20m",  "32Mi",  "64Mi"),
        KubeStateMetrics: new("50m",  "64Mi",  "128Mi"),
        NodeExporter:     new("20m",  "32Mi",  "64Mi"),
        Operator:         new("50m",  "64Mi",  "128Mi"),
        NodeSelector: null);

    public static readonly MonitoringProfile Full = new(
        PrometheusPvcSize: "30Gi",
        GrafanaPvcSize: "10Gi",
        AlertmanagerPvcSize: "5Gi",
        Prometheus:       new("300m", "1.5Gi", "3Gi"),
        // Same Grafana 13 startup peak as Light — 256Mi limit is too tight.
        Grafana:          new("50m",  "256Mi", "512Mi"),
        Alertmanager:     new("20m",  "64Mi",  "128Mi"),
        KubeStateMetrics: new("50m",  "128Mi", "256Mi"),
        NodeExporter:     new("20m",  "64Mi",  "128Mi"),
        Operator:         new("50m",  "128Mi", "256Mi"),
        NodeSelector: new() { ["workload"] = "system" });
}

/// <summary>
/// Helpers for the kube-prometheus-stack Helm release and the Grafana dashboard
/// ConfigMaps that ship alongside it.
/// </summary>
public static class Monitoring
{
    /// <summary>
    /// Builds the Helm values dictionary for the kube-prometheus-stack chart. The
    /// resulting Grafana instance has anonymous Admin access and no login form —
    /// authentication happens at the edge (Cloudflare Access on oci-prod, LAN
    /// trust on local).
    /// </summary>
    public static InputMap<object> BuildValues(MonitoringProfile profile, string storageClass)
    {
        static Dictionary<string, object> Resources(ResourceShape s) => new()
        {
            ["requests"] = new Dictionary<string, object>
            {
                ["cpu"] = s.CpuRequest,
                ["memory"] = s.MemoryRequest,
            },
            ["limits"] = new Dictionary<string, object>
            {
                ["memory"] = s.MemoryLimit,
            },
        };

        Dictionary<string, object> PvcSpec(string size) => new()
        {
            ["spec"] = new Dictionary<string, object>
            {
                ["storageClassName"] = storageClass,
                ["accessModes"] = new[] { "ReadWriteOnce" },
                ["resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new Dictionary<string, object>
                    {
                        ["storage"] = size,
                    },
                },
            },
        };

        // Only emit a nodeSelector when the profile specifies one; on a single-
        // node k3s cluster, an unmatched workload label would leave the pods
        // Pending.
        void AddNodeSelector(Dictionary<string, object> target)
        {
            if (profile.NodeSelector is { Count: > 0 } nodeSelector)
            {
                target["nodeSelector"] = nodeSelector;
            }
        }

        var prometheusSpec = new Dictionary<string, object>
        {
            ["retention"] = "7d",
            ["resources"] = Resources(profile.Prometheus),
            ["storageSpec"] = new Dictionary<string, object>
            {
                ["volumeClaimTemplate"] = PvcSpec(profile.PrometheusPvcSize),
            },
            // Discover ServiceMonitors / PodMonitors / PrometheusRules across all
            // namespaces — the lobbies and game charts ship their own SM/PM in
            // their own namespaces.
            ["serviceMonitorSelectorNilUsesHelmValues"] = false,
            ["podMonitorSelectorNilUsesHelmValues"] = false,
            ["ruleSelectorNilUsesHelmValues"] = false,
        };
        AddNodeSelector(prometheusSpec);

        var grafanaValues = new Dictionary<string, object>
        {
            ["resources"] = Resources(profile.Grafana),
            ["persistence"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["storageClassName"] = storageClass,
                ["accessModes"] = new[] { "ReadWriteOnce" },
                ["size"] = profile.GrafanaPvcSize,
            },
            // Anonymous Admin, no login form. Cloudflare Access (oci-prod) or LAN
            // trust (local) is the real authentication boundary.
            ["grafana.ini"] = new Dictionary<string, object>
            {
                ["auth.anonymous"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["org_role"] = "Admin",
                },
                ["auth"] = new Dictionary<string, object>
                {
                    ["disable_login_form"] = true,
                    ["disable_signout_menu"] = true,
                },
                ["users"] = new Dictionary<string, object>
                {
                    ["allow_sign_up"] = false,
                },
                ["security"] = new Dictionary<string, object>
                {
                    ["allow_embedding"] = true,
                },
            },
            // Auto-import dashboards from any ConfigMap labeled grafana_dashboard=1
            // — the Monitoring.InstallDashboards helper creates one per JSON.
            ["sidecar"] = new Dictionary<string, object>
            {
                ["dashboards"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["label"] = "grafana_dashboard",
                    ["labelValue"] = "1",
                    ["searchNamespace"] = "ALL",
                },
            },
        };
        AddNodeSelector(grafanaValues);

        var alertmanagerSpec = new Dictionary<string, object>
        {
            ["resources"] = Resources(profile.Alertmanager),
            ["storage"] = new Dictionary<string, object>
            {
                ["volumeClaimTemplate"] = PvcSpec(profile.AlertmanagerPvcSize),
            },
        };
        AddNodeSelector(alertmanagerSpec);

        var ksm = new Dictionary<string, object>
        {
            ["resources"] = Resources(profile.KubeStateMetrics),
        };
        AddNodeSelector(ksm);

        var operatorValues = new Dictionary<string, object>
        {
            ["resources"] = Resources(profile.Operator),
        };
        AddNodeSelector(operatorValues);

        return new InputMap<object>
        {
            ["prometheus"] = new Dictionary<string, object>
            {
                ["prometheusSpec"] = prometheusSpec,
            },
            ["grafana"] = grafanaValues,
            ["alertmanager"] = new Dictionary<string, object>
            {
                ["alertmanagerSpec"] = alertmanagerSpec,
            },
            // Bundled alert rules off — Alertmanager is installed for later use,
            // but we don't ship a default ruleset.
            ["defaultRules"] = new Dictionary<string, object>
            {
                ["create"] = false,
            },
            ["kube-state-metrics"] = ksm,
            ["prometheus-node-exporter"] = new Dictionary<string, object>
            {
                ["resources"] = Resources(profile.NodeExporter),
                // DaemonSet — tolerate every taint so it runs on the gameserver
                // node pool too.
                ["tolerations"] = new object[]
                {
                    new Dictionary<string, object> { ["operator"] = "Exists" },
                },
            },
            ["prometheusOperator"] = operatorValues,
        };
    }

    /// <summary>
    /// Creates a ServiceMonitor for the Envoy Gateway control plane and a
    /// PodMonitor for the data-plane Envoy proxies. The upstream
    /// <c>envoyproxy/gateway</c> Helm chart only stamps legacy
    /// <c>prometheus.io/scrape</c> annotations onto the pods, which
    /// kube-prometheus-stack does not honor by default — so we bridge it
    /// here with proper Prometheus Operator CRDs.
    ///
    /// Metrics endpoint reference (Envoy Gateway docs):
    /// • Control plane: port 19001, path <c>/metrics</c>
    /// • Data plane:    port 19001, path <c>/stats/prometheus</c>
    /// </summary>
    public static ConfigGroup InstallEnvoyGatewayMonitors(
        Provider k8s,
        string envoyGatewayNamespace,
        string monitoringNamespace,
        Resource dependsOn)
    {
        var yaml = $@"
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: envoy-gateway-controller
  namespace: {monitoringNamespace}
  labels:
    release: monitoring
spec:
  namespaceSelector:
    matchNames: [{envoyGatewayNamespace}]
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
    matchNames: [{envoyGatewayNamespace}]
  selector:
    matchLabels:
      app.kubernetes.io/component: proxy
      app.kubernetes.io/managed-by: envoy-gateway
  podMetricsEndpoints:
    - port: metrics
      path: /stats/prometheus
      interval: 30s
";

        return new ConfigGroup("envoy-gateway-monitors", new ConfigGroupArgs
        {
            Yaml = yaml,
        }, new ComponentResourceOptions
        {
            Provider = k8s,
            DependsOn = { dependsOn },
        });
    }

    /// <summary>
    /// Creates a labeled ConfigMap per JSON file in <c>infra/dashboards/</c>.
    /// The Grafana sidecar watches for the <c>grafana_dashboard=1</c> label and
    /// auto-imports the contents. If the directory is missing or empty, this is
    /// a no-op — Grafana still has its chart-bundled dashboards.
    /// </summary>
    public static List<ConfigMap> InstallDashboards(
        Provider k8s,
        string monitoringNamespace,
        Resource dependsOn)
    {
        var configMaps = new List<ConfigMap>();
        var dashboardsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "dashboards"));
        if (!Directory.Exists(dashboardsDir))
        {
            return configMaps;
        }

        foreach (var jsonPath in Directory.EnumerateFiles(dashboardsDir, "*.json").OrderBy(p => p))
        {
            var fileName = Path.GetFileName(jsonPath);
            // ConfigMap names must be DNS-1123 — JSON file names use dots which
            // are allowed in CM names but not in label values, so use the dotless
            // base name for the resource name.
            var resourceName = "dashboard-" +
                Path.GetFileNameWithoutExtension(fileName)
                    .ToLowerInvariant()
                    .Replace('.', '-')
                    .Replace('_', '-');
            var contents = File.ReadAllText(jsonPath);
            configMaps.Add(new ConfigMap(resourceName, new ConfigMapArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = resourceName,
                    Namespace = monitoringNamespace,
                    Labels =
                    {
                        ["grafana_dashboard"] = "1",
                    },
                },
                Data = { [fileName] = contents },
            }, new CustomResourceOptions
            {
                Provider = k8s,
                DependsOn = { dependsOn },
            }));
        }
        return configMaps;
    }
}
