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
/// Handles for the external-dns install: the external-dns namespace name and
/// the Helm release used by downstream <c>DependsOn</c> wiring.
/// </summary>
public sealed record ExternalDnsResources(
    string Namespace,
    HelmRelease Release);

/// <summary>
/// Installs external-dns (Cloudflare provider) via Helm. Only the oci-prod
/// stack uses this — Program.cs gates the call. The chart's env block
/// references the <c>external-dns-cloudflare</c> Secret that this module
/// creates from the Pulumi-config Cloudflare API token. Chart values live in
/// <c>infra/values/oci-prod/external-dns.yaml</c> (no shared/common file —
/// nothing else consumes external-dns config). <c>envoyGatewayCrds</c> is in
/// <c>DependsOn</c> because <c>sources: [gateway-httproute]</c> won't
/// reconcile without the Gateway API CRDs installed.
/// </summary>
public static class ExternalDns
{
    private const string ChartRepo = "https://kubernetes-sigs.github.io/external-dns/";
    private const string Chart = "external-dns";
    private const string ReleaseName = "external-dns";
    private const string NamespaceName = "external-dns";
    private const string SecretName = "external-dns-cloudflare";

    public static ExternalDnsResources Deploy(
        Pulumi.Config config,
        Provider k8s,
        Output<string> cloudflareApiToken,
        Resource envoyGatewayCrds,
        CustomResourceOptions providerOpts)
    {
        var stackValuesPath = StackValues.ResolveStackOnly(
            "external-dns.yaml", "external-dns", "ExternalDns.Deploy");

        var ns = new Namespace("external-dns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = NamespaceName },
        }, providerOpts);

        var cloudflareSecret = new Secret("external-dns-cloudflare", new SecretArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Name = SecretName,
                Namespace = NamespaceName,
            },
            StringData = { ["cloudflare_api_token"] = cloudflareApiToken },
        }, new CustomResourceOptions { Provider = k8s, DependsOn = { ns } });

        // Chart version intentionally unset — matches existing behavior. Pin
        // in a follow-up when the floating-latest is no longer desirable.
        var release = new HelmRelease("external-dns", new HelmReleaseArgs
        {
            Name = ReleaseName,
            Chart = Chart,
            RepositoryOpts = new V3RepositoryOptsArgs { Repo = ChartRepo },
            Namespace = NamespaceName,
            ValueYamlFiles = new InputList<AssetOrArchive>
            {
                new FileAsset(stackValuesPath),
            },
        }, new CustomResourceOptions
        {
            Provider = k8s,
            DependsOn = { envoyGatewayCrds, cloudflareSecret },
        });

        return new ExternalDnsResources(NamespaceName, release);
    }
}
