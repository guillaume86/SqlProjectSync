using System.Reflection;

namespace SqlProjectSync.IntegrationTests;

internal static class RepoLayout
{
    public static string SolutionRoot { get; } = FindSolutionRoot();

    public static string SdkFixtureDirectory => Path.Combine(
        SolutionRoot, "tests", "Fixtures", "SdkStyleTestProject");

    public static string SdkFixtureProject => Path.Combine(
        SdkFixtureDirectory, "SdkStyleTestProject.sqlproj");

    public static string SdkFixtureScmp => Path.Combine(
        SdkFixtureDirectory, "CompareToProject.scmp");

    public static string LegacyFixtureDirectory => Path.Combine(
        SolutionRoot, "tests", "Fixtures", "LegacyTestProject");

    public static string LegacyFixtureProject => Path.Combine(
        LegacyFixtureDirectory, "LegacyTestProject.sqlproj");

    public static string LegacyFixtureScmp => Path.Combine(
        LegacyFixtureDirectory, "CompareToProject.scmp");

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.slnx").Any())
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate a .slnx walking up from the test assembly location.");
    }
}
