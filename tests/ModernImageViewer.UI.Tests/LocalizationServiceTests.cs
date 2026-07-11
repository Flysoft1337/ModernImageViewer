using System.ComponentModel;
using System.Globalization;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

[CollectionDefinition("Culture", DisableParallelization = true)]
public sealed class CultureTestGroup;

[Collection("Culture")]
public sealed class LocalizationServiceTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-TW", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("invalid", "en-US")]
    public void NormalizeCultureNameReturnsSupportedCulture(string input, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizeCultureName(input));
    }

    [Fact]
    public void InitializeUsesSavedLanguageBeforeSystemCulture()
    {
        using CultureScope scope = new("en-US");
        InMemoryUserSettings settings = new("zh-CN");
        LocalizationService service = new(settings);

        service.Initialize();

        Assert.Equal("zh-CN", service.CurrentCulture.Name);
        Assert.Equal("现代图片查看器", service.GetString("MainWindow_Title"));
    }

    [Fact]
    public void UnsupportedCultureFallsBackToEnglish()
    {
        using CultureScope scope = new("fr-FR");
        LocalizationService service = new(new InMemoryUserSettings());

        service.Initialize();

        Assert.Equal("en-US", service.CurrentCulture.Name);
        Assert.Equal("Modern Image Viewer", service.GetString("MainWindow_Title"));
    }

    [Fact]
    public void SwitchingCultureUpdatesViewModelAndSavesPreference()
    {
        using CultureScope scope = new("en-US");
        InMemoryUserSettings settings = new();
        LocalizationService service = new(settings);
        service.Initialize();
        ImageOpenCoordinator coordinator = new(new NullFilePicker(), new FailingImageDecoder());
        MainWindowViewModel viewModel = new(service, coordinator, new ImageBrowseSession());
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.SelectedLanguage = service.SupportedLanguages.Single(x => x.CultureName == "zh-CN");

        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal("现代图片查看器", viewModel.Title);
        Assert.Equal("打开图片", viewModel.EmptyTitle);
        Assert.Contains(string.Empty, changedProperties);
    }

    [Theory]
    [InlineData("en-US", "Language")]
    [InlineData("zh-CN", "语言")]
    public void ResourcesContainLanguageLabel(string cultureName, string expected)
    {
        using CultureScope scope = new("en-US");
        LocalizationService service = new(new InMemoryUserSettings(cultureName));
        service.Initialize();

        Assert.Equal(expected, service.GetString("Language_Label"));
    }

    private sealed class NullFilePicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FailingImageDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromException<PixelBuffer>(new NotSupportedException());
    }

    private sealed class InMemoryUserSettings(string? language = null) : IUserSettingsService
    {
        public string? Language { get; private set; } = language;

        public void SaveLanguage(string value)
        {
            Language = value;
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        public CultureScope(string cultureName)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
            CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
        }
    }
}
