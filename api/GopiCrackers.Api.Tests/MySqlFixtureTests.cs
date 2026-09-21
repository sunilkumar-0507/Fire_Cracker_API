using MySqlConnector;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Tests for the safety rail on the MySQL suite, rather than for the API.
///
/// <see cref="MySqlFixture"/> is pointed at a real server by whoever runs it,
/// and the only thing keeping it off that server's live data is the rule that
/// it works in a scratch database beside it. These run everywhere, including
/// where no server is configured, because the rule has to hold before anyone
/// finds out whether it did.
/// </summary>
public sealed class MySqlFixtureTests
{
    private const string Live =
        "Server=203.0.113.10;Port=3306;Database=gopicrackers;User ID=gopi;Password=hunter2;";

    private static string DatabaseIn(string connectionString) =>
        new MySqlConnectionStringBuilder(connectionString).Database;

    [Fact]
    public void The_scratch_database_is_never_the_one_that_was_configured()
    {
        var redirected = MySqlFixture.Redirect(Live);

        Assert.NotEqual("gopicrackers", DatabaseIn(redirected));
        Assert.Equal("gopicrackers" + MySqlFixture.ScratchSuffix, DatabaseIn(redirected));
    }

    [Fact]
    public void The_scratch_database_always_carries_the_suffix_the_drop_checks_for()
    {
        foreach (var name in (string[])["shop", "gopi_crackers", "a", "GopiCrackers"])
        {
            var configured = $"Server=localhost;Database={name};User ID=u;Password=p;";

            Assert.EndsWith(MySqlFixture.ScratchSuffix,
                DatabaseIn(MySqlFixture.Redirect(configured)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Redirecting_an_already_redirected_string_does_not_stack_suffixes()
    {
        var once = MySqlFixture.Redirect(Live);
        var twice = MySqlFixture.Redirect(once);

        Assert.Equal(DatabaseIn(once), DatabaseIn(twice));
    }

    [Fact]
    public void A_connection_string_naming_no_database_still_gets_a_scratch_one()
    {
        var serverOnly = "Server=localhost;Port=3306;User ID=u;Password=p;";

        Assert.Equal("gopicrackers" + MySqlFixture.ScratchSuffix,
            DatabaseIn(MySqlFixture.Redirect(serverOnly)));
    }

    [Fact]
    public void Redirecting_changes_the_database_and_nothing_else()
    {
        var original = new MySqlConnectionStringBuilder(Live);
        var redirected = new MySqlConnectionStringBuilder(MySqlFixture.Redirect(Live));

        Assert.Equal(original.Server, redirected.Server);
        Assert.Equal(original.Port, redirected.Port);
        Assert.Equal(original.UserID, redirected.UserID);
        Assert.Equal(original.Password, redirected.Password);
    }

    /// <summary>
    /// The suite must skip when no server is configured and run when one is.
    /// Getting this backwards would either fail every build or — far worse —
    /// pass silently having tested nothing, which is the failure this whole
    /// file exists to rule out.
    /// </summary>
    [Fact]
    public void The_suite_skips_exactly_when_no_server_is_configured()
    {
        if (MySqlFixture.ServerConnectionString is null)
            Assert.NotNull(MySqlFixture.SkipReason);
        else
            Assert.Null(MySqlFixture.SkipReason);
    }
}
