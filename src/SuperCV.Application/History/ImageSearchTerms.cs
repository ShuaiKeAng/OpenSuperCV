namespace SuperCV.Application.History;

/// <summary>
/// Identifies search terms that should include clipboard image entries.
/// </summary>
public static class ImageSearchTerms
{
    private static readonly HashSet<string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        "图片",
        "图",
        "图像",
        "图形",
        "照片",
        "相片",
        "截图",
        "屏幕截图",
        "插图",
        "配图",
        "影像",
        "image",
        "images",
        "picture",
        "pictures",
        "photo",
        "photos",
        "photograph",
        "photographs",
        "screenshot",
        "screenshots",
        "graphic",
        "graphics",
        "pic",
        "pics",
        "img",
    };

    public static bool IncludesImages(string searchText) => Terms.Contains(searchText);
}
