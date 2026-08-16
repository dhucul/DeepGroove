using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Serializes every test class that redirects <see cref="WaveLab.Util.AppSettings.AppDataDir"/>.
/// That root — and the cached instance behind it — is static, so two such classes
/// running in parallel would read each other's sandbox.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppSettingsCollection
{
    public const string Name = "AppSettings sandbox";
}
