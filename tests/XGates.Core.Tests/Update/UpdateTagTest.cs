using XGates.Core.Update;

namespace XGates.Core.Tests.Update;

/// <summary>
/// Порт UpdateTagTest.kt (адреса — репозиторий Windows-версии). Запасной путь обновлялки: адрес установщика строится по
/// тегу из редиректа `latest/download/update.json`, а не по `latest/download/&lt;файл&gt;` (тот даёт 404, как только
/// выходит релиз новее).
/// </summary>
public class UpdateTagTest
{
    [Fact]
    public void Tag_is_taken_from_github_redirect_of_latest_download()
    {
        var chain = new[]
        {
            "https://github.com/pico-soft/X-Gates-Win/releases/latest/download/update.json",
            "https://github.com/pico-soft/X-Gates-Win/releases/download/v0.62/update.json",
            "https://release-assets.githubusercontent.com/github-production-release-asset/1335017878/f5d7?sp=r&sv=2018",
        };
        Assert.Equal("v0.62", UpdateChecker.tagFromRedirects(chain));
    }

    [Fact]
    public void No_tag_without_redirect_chain()
    {
        Assert.Null(UpdateChecker.tagFromRedirects([]));
        Assert.Null(UpdateChecker.tagFromRedirects(["https://github.com/pico-soft/X-Gates-Win/releases/latest/download/update.json"]));
    }

    [Fact]
    public void Tag_download_url_points_at_that_release()
    {
        var repo = new UpdateChecker.Repo("pico-soft", "X-Gates-Win");
        Assert.Equal(
            "https://github.com/pico-soft/X-Gates-Win/releases/download/v0.62/XGatesWin-0.62-win-x64.msi",
            $"{repo.tagDownload("v0.62")}/XGatesWin-0.62-win-x64.msi");
    }

    [Fact]
    public void Default_repo_is_the_windows_release_repo()
    {
        Assert.Equal("pico-soft/X-Gates-Win", Assert.Single(UpdateChecker.REPOS).slug);
        Assert.Equal("https://api.github.com/repos/pico-soft/X-Gates-Win/releases/latest", UpdateChecker.REPOS[0].apiLatest);
    }
}
