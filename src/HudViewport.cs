namespace Gh3Skin;

internal readonly record struct HudViewport(float Scale, float OffsetX, float CenterOffsetY, float BottomOffsetY)
{
    public static HudViewport For(int width, int height)
    {
        var scale = Math.Min(width / 1920f, height / 1080f);
        var offsetX = (width - 1920 * scale) / 2;
        var bottomOffsetY = height - 1080 * scale;
        return new HudViewport(scale, offsetX, bottomOffsetY / 2, bottomOffsetY);
    }

    public static HudViewport ForHighway(int screenHeight, float cameraX, float cameraY,
        float cameraWidth, float cameraHeight, float fieldOfView)
    {
        var zoom = MathF.Tan(HighwayView.FieldOfView * MathF.PI / 360) / MathF.Tan(fieldOfView * MathF.PI / 360);
        var scale = cameraHeight / 1080f * zoom;
        var offsetY = screenHeight - cameraY - cameraHeight / 2 - 540 * scale;
        return new HudViewport(scale, cameraX + cameraWidth / 2 - 960 * scale, offsetY, offsetY);
    }
}
