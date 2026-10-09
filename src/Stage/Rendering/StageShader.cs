using UnityEngine;

namespace Gh3Skin;

internal sealed class StageShaderUnsupportedException : Exception
{
    public StageShaderUnsupportedException(string message) : base(message) { }
}

internal static class StageShader
{
    // pipeline/render/shaders.py names every pass of the GH3 shader bundles. When a bundle has no program for the
    // active graphics API, Unity uses its error shader (pink, unnamed passes) and Shader.isSupported stays true.
    private const string PassName = "GH3";

    public static Shader Load(AssetBundle bundle, string description)
    {
        var shader = bundle.mainAsset?.Cast<Shader>() ?? throw new InvalidDataException($"{description} is missing");
        shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            if (shader.isSupported && material.passCount > 0 && material.GetPassName(0) == PassName) return shader;
        }
        finally
        {
            UnityEngine.Object.Destroy(material);
        }
        throw new StageShaderUnsupportedException(
            $"GH3 stage is off: {description} has no program for the {SystemInfo.graphicsDeviceType} graphics API. " +
            "The stage supports OpenGL Core and Direct3D 11. On Windows, start Clone Hero without a graphics API option " +
            "or with -force-d3d11 or -force-glcore. If the problem continues, run scripts/regenerate.sh render.");
    }
}
