using System.IO;
using Pulumi;

namespace YARG.Online.Infrastructure;

/// <summary>
/// Helpers for resolving the per-stack Helm values files under
/// <c>infra/values/{common,stack}/</c> that modules load via Pulumi's
/// <c>ValueYamlFiles</c>.
/// </summary>
public static class StackValues
{
    /// <summary>
    /// Resolves and validates the shared + stack-specific values file pair for
    /// a Helm module. Throws <see cref="FileNotFoundException"/> if either file
    /// is missing — chart installs fail loudly rather than silently using
    /// defaults that don't fit the stack.
    /// </summary>
    /// <param name="fileName">Values file name, e.g. <c>monitoring.yaml</c>.</param>
    /// <param name="moduleDisplayName">Module name used in the error message,
    /// lowercase mid-sentence form (e.g. <c>monitoring</c>, <c>Envoy Gateway</c>).
    /// First character is uppercased when used at the start of a sentence.</param>
    /// <param name="deployMethodName">Fully qualified deploy method (e.g.
    /// <c>Monitoring.Deploy</c>) named in the stack-missing hint.</param>
    public static (string CommonPath, string StackPath) ResolveCommonAndStack(
        string fileName,
        string moduleDisplayName,
        string deployMethodName)
    {
        var stackName = Deployment.Instance.StackName;
        var cwd = Directory.GetCurrentDirectory();
        var commonPath = Path.Combine(cwd, "values", "common", fileName);
        var stackPath = Path.Combine(cwd, "values", stackName, fileName);

        var sentenceStart = char.ToUpperInvariant(moduleDisplayName[0]) + moduleDisplayName[1..];

        if (!File.Exists(commonPath))
        {
            throw new FileNotFoundException(
                $"Shared {moduleDisplayName} values file not found at '{commonPath}'.",
                commonPath);
        }
        if (!File.Exists(stackPath))
        {
            throw new FileNotFoundException(
                $"{sentenceStart} values file for stack '{stackName}' not found at '{stackPath}'. " +
                $"Add it under infra/values/{{stack}}/ or extend {deployMethodName} to cover this stack.",
                stackPath);
        }

        return (commonPath, stackPath);
    }

    /// <summary>
    /// Resolves and validates a single per-stack values file for a Helm module
    /// that has no shared/common values. Throws <see cref="FileNotFoundException"/>
    /// if the file is missing — chart installs fail loudly rather than silently
    /// using defaults that don't fit the stack.
    /// </summary>
    /// <param name="fileName">Values file name, e.g. <c>external-dns.yaml</c>.</param>
    /// <param name="moduleDisplayName">Module name used in the error message,
    /// lowercase mid-sentence form (e.g. <c>external-dns</c>).
    /// First character is uppercased when used at the start of a sentence.</param>
    /// <param name="deployMethodName">Fully qualified deploy method (e.g.
    /// <c>ExternalDns.Deploy</c>) named in the stack-missing hint.</param>
    public static string ResolveStackOnly(
        string fileName,
        string moduleDisplayName,
        string deployMethodName)
    {
        var stackName = Deployment.Instance.StackName;
        var cwd = Directory.GetCurrentDirectory();
        var stackPath = Path.Combine(cwd, "values", stackName, fileName);

        var sentenceStart = char.ToUpperInvariant(moduleDisplayName[0]) + moduleDisplayName[1..];

        if (!File.Exists(stackPath))
        {
            throw new FileNotFoundException(
                $"{sentenceStart} values file for stack '{stackName}' not found at '{stackPath}'. " +
                $"Add it under infra/values/{{stack}}/ or extend {deployMethodName} to cover this stack.",
                stackPath);
        }

        return stackPath;
    }
}
