namespace AlarmPushCenter;

public static class ProductDisclaimer
{
    public static string Text
    {
        get
        {
            using var stream = typeof(ProductDisclaimer).Assembly.GetManifestResourceStream("AlarmPushCenter.Disclaimer.txt")
                ?? throw new InvalidOperationException("免责声明资源缺失。");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
