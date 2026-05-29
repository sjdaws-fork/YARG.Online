using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using HelmRelease = Pulumi.Kubernetes.Helm.V3.Release;
using HelmReleaseArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.ReleaseArgs;
using V3RepositoryOptsArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.RepositoryOptsArgs;

namespace YARG.Online.Infrastructure;

/// <summary>
/// Handles for the kube-prometheus-stack install: the monitoring namespace,
/// the chart version (exposed as a stack output), and the Helm release used
/// by downstream <c>DependsOn</c> wiring.
/// </summary>
public sealed record MonitoringResources(
    string Namespace,
    string Version,
    HelmRelease Release);

/// <summary>
/// Installs kube-prometheus-stack via Helm. Sizing, storage class, and node
/// selectors are stack-specific and large, so they live in
/// <c>infra/values/common/monitoring.yaml</c> (shared) plus
/// <c>infra/values/{stackName}/monitoring.yaml</c> (overrides), loaded via
/// <c>ValueYamlFiles</c>. The release name <c>monitoring</c> is referenced
/// by the lobbies/game ServiceMonitor/PodMonitor selectors and by in-cluster
/// Service DNS (<c>monitoring-grafana</c>, <c>monitoring-kube-prometheus-prometheus</c>),
/// so it stays a code-level constant.
/// </summary>
public static class Monitoring
{
    private const string ChartRepo = "https://prometheus-community.github.io/helm-charts";
    private const string Chart = "kube-prometheus-stack";
    private const string ReleaseName = "monitoring";

    // Default chart version. Pin via Pulumi config (kubePrometheusStackVersion)
    // when bumping is intentional rather than implicit.
    private const string DefaultVersion = "85.2.2";

    public static MonitoringResources Deploy(
        Pulumi.Config config,
        Provider k8s,
        string monitoringNamespaceName,
        CustomResourceOptions providerOpts)
    {
        var version = config.Get("kubePrometheusStackVersion") ?? DefaultVersion;

        var (commonValuesPath, stackValuesPath) = StackValues.ResolveCommonAndStack(
            "monitoring.yaml", "monitoring", "Monitoring.Deploy");

        var monitoringNs = new Namespace("monitoring", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = monitoringNamespaceName },
        }, providerOpts);

        // ValueYamlFiles list order matters: later files override earlier on
        // overlapping keys, so the stack file wins over common.
        var release = new HelmRelease("kube-prometheus-stack", new HelmReleaseArgs
        {
            Name = ReleaseName,
            Chart = Chart,
            Version = version,
            RepositoryOpts = new V3RepositoryOptsArgs { Repo = ChartRepo },
            Namespace = monitoringNamespaceName,
            ValueYamlFiles = new InputList<AssetOrArchive>
            {
                new FileAsset(commonValuesPath),
                new FileAsset(stackValuesPath),
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            DependsOn = { monitoringNs },
        });

        return new MonitoringResources(monitoringNamespaceName, version, release);
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
