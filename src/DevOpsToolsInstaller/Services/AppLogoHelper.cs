using System;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DevOpsToolsInstaller.Services;

public static class AppLogoHelper
{
    public static BitmapImage GetLogoImage()
    {
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
            if (File.Exists(logoPath))
            {
                return new BitmapImage(new Uri(logoPath, UriKind.Absolute));
            }
        }
        catch { }

        try
        {
            return new BitmapImage(new Uri("ms-appx:///Assets/app.png"));
        }
        catch { }

        return new BitmapImage();
    }

    public static BitmapImage GetAuthorImage()
    {
        try
        {
            var authorPath = Path.Combine(AppContext.BaseDirectory, "Assets", "author.png");
            if (File.Exists(authorPath))
            {
                return new BitmapImage(new Uri(authorPath));
            }
        }
        catch { }

        // No bundled portrait. Fall back to this fork maintainer's avatar rather
        // than the original author's.
        return new BitmapImage(new Uri("https://avatars.githubusercontent.com/u/ayu-haker?v=4"));
    }
}
