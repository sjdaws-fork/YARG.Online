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
/// Handles for the Agones install: the agones-system namespace name, the
/// chart version (exposed as a stack output), and the Helm release used by
/// downstream <c>DependsOn</c> wiring.
/// </summary>
public sealed record AgonesResources(
    string Namespace,
    string Version,
    HelmRelease Release);

/// <summary>
/// Installs Agones via Helm. The chart bundles its CRDs in its templates
/// (not under <c>crds/</c>), so a single Release covers both. Replicas,
/// allocator service type, ping toggle, ServiceMonitor toggle, and
/// nodeSelectors live in <c>infra/values/common/agones.yaml</c> (shared)
/// plus <c>infra/values/{stackName}/agones.yaml</c> (overrides), loaded via
/// <c>ValueYamlFiles</c>. <c>monitoringDependency</c> is required because
/// the chart's <c>metrics.serviceMonitor</c> resource needs the Prometheus
/// Operator CRDs that kube-prometheus-stack installs.
/// </summary>
public static class Agones
{
    private const string ChartRepo = "https://agones.dev/chart/stable";
    private const string Chart = "agones";
    private const string ReleaseName = "agones";

    // Default chart version. Pin via Pulumi config (agonesVersion) when
    // bumping is intentional rather than implicit.
    private const string DefaultVersion = "1.57.0";

    public static AgonesResources Deploy(
        Pulumi.Config config,
        Provider k8s,
        string agonesNamespaceName,
        Resource monitoringDependency,
        CustomResourceOptions providerOpts)
    {
        var version = config.Get("agonesVersion") ?? DefaultVersion;

        var (commonValuesPath, stackValuesPath) = StackValues.ResolveCommonAndStack(
            "agones.yaml", "Agones", "Agones.Deploy");

        var ns = new Namespace("agones-system", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = agonesNamespaceName },
        }, providerOpts);

        // ValueYamlFiles list order matters: later files override earlier on
        // overlapping keys, so the stack file wins over common.
        var release = new HelmRelease("agones", new HelmReleaseArgs
        {
            Name = ReleaseName,
            Chart = Chart,
            Version = version,
            RepositoryOpts = new V3RepositoryOptsArgs { Repo = ChartRepo },
            Namespace = agonesNamespaceName,
            ValueYamlFiles = new InputList<AssetOrArchive>
            {
                new FileAsset(commonValuesPath),
                new FileAsset(stackValuesPath),
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            DependsOn = { ns, monitoringDependency },
        });

        return new AgonesResources(agonesNamespaceName, version, release);
    }
}
