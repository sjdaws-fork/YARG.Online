using System;
using System.Collections.Generic;
using System.IO;
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
/// Handles for the Postgres install: the database namespace and the in-cluster
/// DNS of the PgBouncer pooler that apps should connect through.
/// </summary>
public sealed record PostgresResources(
    string DatabaseNamespace,
    string PoolerHost,
    int PoolerPort);

/// <summary>
/// Installs CloudNativePG (operator + Cluster + PgBouncer pooler) via two Helm
/// releases. The operator chart values are static across stacks and live here
/// in code; the Cluster/Pooler values are large and stack-specific, so they
/// live in <c>infra/values/{stackName}/postgres.yaml</c> and are loaded via
/// <c>ValueYamlFiles</c>.
/// </summary>
public static class Postgres
{
    private const string ChartRepo = "https://cloudnative-pg.github.io/charts";
    private const string OperatorChart = "cloudnative-pg";
    private const string ClusterChart = "cluster";

    // Default chart versions. Pin via Pulumi config (cnpgOperatorVersion /
    // cnpgClusterVersion) when bumping is intentional rather than implicit.
    // The cluster chart was pinned to 0.0.11 before — that version has a
    // template bug that renders `cluster.postgresql` (the whole object,
    // including child maps) under `spec.postgresql.parameters`, which the
    // CNPG admission webhook rejects. 0.6.1+ uses
    // `.Values.cluster.postgresql.parameters` correctly.
    private const string DefaultOperatorVersion = "0.28.2";
    private const string DefaultClusterVersion = "0.6.1";

    // Helm release name for the Cluster chart. The chart names the Cluster
    // CR `{release}-cluster` and the Pooler Service `{release}-cluster-pooler-{poolerName}`.
    private const string ClusterReleaseName = "postgres";
    private const string PoolerName = "rw";

    public static PostgresResources Deploy(
        Pulumi.Config config,
        Provider k8s,
        bool provisionOke,
        string cnpgNamespaceName,
        string databaseNamespaceName,
        Resource monitoringDependency,
        CustomResourceOptions providerOpts)
    {
        if (provisionOke)
        {
            // The oci-prod cluster values pin Postgres to a `workload=database`
            // node pool that has not been provisioned in Oracle.cs yet. Stand
            // up that pool (and decide its shape — VM.Standard.A1.Flex sizing,
            // taint, etc.) before removing this guard.
            throw new NotImplementedException("oci node provisioning not yet determined");
        }

        var operatorVersion = config.Get("cnpgOperatorVersion") ?? DefaultOperatorVersion;
        var clusterVersion = config.Get("cnpgClusterVersion") ?? DefaultClusterVersion;

        var stackName = Deployment.Instance.StackName;
        var valuesPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "values",
            stackName,
            "postgres.yaml");
        if (!File.Exists(valuesPath))
        {
            throw new FileNotFoundException(
                $"Postgres values file for stack '{stackName}' not found at '{valuesPath}'. " +
                "Add it under infra/values/{stack}/ or extend Postgres.Deploy to cover this stack.",
                valuesPath);
        }

        var cnpgNs = new Namespace("cnpg-system", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = cnpgNamespaceName },
        }, providerOpts);

        // Operator install. Monitoring flags wire CNPG into the existing
        // kube-prometheus-stack: a PodMonitor for the operator itself plus a
        // labeled ConfigMap holding the official CNPG Grafana dashboard, which
        // the Grafana sidecar auto-discovers via grafana_dashboard=1.
        var cnpgOperator = new HelmRelease("cnpg-operator", new HelmReleaseArgs
        {
            Name = "cloudnative-pg",
            Chart = OperatorChart,
            Version = operatorVersion,
            RepositoryOpts = new V3RepositoryOptsArgs { Repo = ChartRepo },
            Namespace = cnpgNamespaceName,
            Values = new InputMap<object>
            {
                ["monitoring"] = new Dictionary<string, object>
                {
                    ["podMonitorEnabled"] = true,
                    ["grafanaDashboard"] = new Dictionary<string, object>
                    {
                        ["create"] = true,
                    },
                },
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            // monitoring must be in place first: the operator chart creates a
            // PodMonitor (needs the Prometheus Operator CRDs) and a dashboard
            // ConfigMap (needs the Grafana sidecar to import it).
            DependsOn = { cnpgNs, monitoringDependency },
        });

        var dbNs = new Namespace("database", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = databaseNamespaceName },
        }, providerOpts);

        // Cluster + Pooler via the cnpg/cluster chart. All knobs live in the
        // stack-specific YAML file — keep this call site free of values.
        var cluster = new HelmRelease("postgres-cluster", new HelmReleaseArgs
        {
            Name = ClusterReleaseName,
            Chart = ClusterChart,
            Version = clusterVersion,
            RepositoryOpts = new V3RepositoryOptsArgs { Repo = ChartRepo },
            Namespace = databaseNamespaceName,
            ValueYamlFiles = new InputList<AssetOrArchive>
            {
                new FileAsset(valuesPath),
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            // Operator must be running before the Cluster CR is created —
            // otherwise the CR sits unreconciled until the operator catches up.
            DependsOn = { dbNs, cnpgOperator },
        });

        // The cnpg/cluster chart names the Cluster CR `{release}-cluster` and
        // the Pooler Service `{release}-cluster-pooler-{poolerName}`. Apps
        // connect through PgBouncer, not the bare Cluster service.
        var poolerHost = $"{ClusterReleaseName}-cluster-pooler-{PoolerName}.{databaseNamespaceName}.svc.cluster.local";

        return new PostgresResources(
            DatabaseNamespace: databaseNamespaceName,
            PoolerHost: poolerHost,
            PoolerPort: 5432);
    }
}
