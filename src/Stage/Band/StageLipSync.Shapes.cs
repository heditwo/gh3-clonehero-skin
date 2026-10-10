using System.Numerics;

namespace Gh3Skin;

// Measured: the mouth shapes of the GH3 singer facial clips (tools/lipsync-shapes.py over the 570 clips).
internal sealed partial class StageLipSync
{
    // Per bone of MouthBones: rotation vector (degrees, local axes, applied after the current rotation) and translation.
    // Open is per degree of jaw opening; Round is the mouth-width component at its 95th percentile (positive: corners in).
    private static readonly float[] Open =
    {
        -0.0212f, -0.0132f, 1.0000f, -0.000013f, -0.000194f, -0.000003f, // Bone_Jaw
        0.0000f, 0.0000f, 0.0000f, -0.000301f, -0.000526f, -0.000060f, // Bone_Mouth_L
        0.0000f, 0.0000f, 0.0000f, -0.000333f, -0.000517f, 0.000013f, // Bone_Mouth_R
        -0.0369f, 0.0062f, 0.7595f, -0.001186f, -0.000729f, -0.000038f, // Bone_Lip_Lower_Mid
        -0.0142f, -0.0114f, 0.0930f, 0.000021f, -0.000076f, -0.000014f, // Bone_Lip_Upper_Mid
        0.1058f, -0.0220f, 0.2523f, -0.000208f, -0.000345f, 0.000006f, // Bone_Cheek_L
        -0.1217f, -0.0348f, 0.2889f, -0.000220f, -0.000290f, -0.000042f, // Bone_Cheek_R
        -0.0671f, 0.1612f, 0.1667f, -0.000080f, -0.000059f, -0.000029f, // Bone_Cheek_Upper_L
        0.0479f, -0.1529f, 0.1516f, -0.000080f, -0.000038f, 0.000004f, // Bone_Cheek_Upper_R
        -0.1645f, -0.6612f, 0.9704f, -0.000817f, -0.000718f, -0.000138f, // Bone_Lip_Lower_Corner_L
        0.1572f, 0.6176f, 0.9351f, -0.000873f, -0.000734f, 0.000090f, // Bone_Lip_Lower_Corner_R
        -0.1169f, -0.2973f, 0.6228f, -0.001074f, -0.000753f, -0.000147f, // Bone_Lip_Lower_L
        0.1169f, 0.1704f, 0.6455f, -0.001114f, -0.000751f, 0.000085f, // Bone_Lip_Lower_R
        -0.3833f, 0.2567f, 0.1961f, -0.000084f, -0.000299f, -0.000116f, // Bone_Lip_Upper_Corner_L
        0.3712f, -0.3017f, 0.1461f, -0.000102f, -0.000292f, 0.000083f, // Bone_Lip_Upper_Corner_R
        -0.1510f, 0.1775f, 0.1904f, -0.000017f, -0.000153f, -0.000113f, // Bone_Lip_Upper_L
        0.1309f, -0.1728f, 0.1321f, -0.000019f, -0.000147f, 0.000089f, // Bone_Lip_Upper_R
        0.0000f, 0.0000f, 0.0059f, -0.001136f, -0.000702f, -0.000037f, // Bone_Tongue
    };
    private static readonly float[] Round =
    {
        -0.0300f, 0.3901f, 0.0000f, -0.001010f, 0.000457f, 0.000080f, // Bone_Jaw
        0.0000f, 0.0000f, 0.0264f, -0.007394f, 0.009765f, 0.007340f, // Bone_Mouth_L
        0.0000f, 0.0000f, 0.0264f, -0.006049f, 0.009544f, -0.006509f, // Bone_Mouth_R
        1.3631f, -1.3606f, 3.2620f, -0.001582f, 0.002455f, 0.001141f, // Bone_Lip_Lower_Mid
        1.4956f, -0.1247f, -7.0595f, -0.003704f, 0.004910f, -0.000090f, // Bone_Lip_Upper_Mid
        5.9089f, -5.8067f, 0.3735f, -0.003361f, 0.001111f, 0.007091f, // Bone_Cheek_L
        -5.4739f, 5.4876f, -0.2291f, -0.002242f, 0.000393f, -0.006477f, // Bone_Cheek_R
        2.9678f, 1.2319f, 3.9205f, -0.004757f, -0.002127f, 0.004842f, // Bone_Cheek_Upper_L
        -2.2896f, -1.9410f, 3.8716f, -0.004123f, -0.002483f, -0.004083f, // Bone_Cheek_Upper_R
        9.3118f, -1.2824f, 13.6397f, -0.003824f, 0.008954f, 0.006762f, // Bone_Lip_Lower_Corner_L
        -7.5532f, 4.0189f, 13.8752f, -0.002521f, 0.008009f, -0.005381f, // Bone_Lip_Lower_Corner_R
        7.3408f, 1.4775f, 14.6518f, -0.002427f, 0.004845f, 0.004639f, // Bone_Lip_Lower_L
        -4.6538f, 0.9963f, 14.0494f, -0.001808f, 0.004143f, -0.002791f, // Bone_Lip_Lower_R
        -1.4339f, 11.8701f, -12.3186f, -0.007167f, 0.008488f, 0.006642f, // Bone_Lip_Upper_Corner_L
        0.8764f, -10.2196f, -9.6503f, -0.007264f, 0.008731f, -0.006493f, // Bone_Lip_Upper_Corner_R
        -2.7327f, 5.4831f, -6.0514f, -0.005255f, 0.005688f, 0.003536f, // Bone_Lip_Upper_L
        0.6051f, -7.6622f, -5.2251f, -0.005758f, 0.006087f, -0.003554f, // Bone_Lip_Upper_R
        0.0000f, -0.0078f, -0.0206f, -0.001445f, 0.002521f, 0.001112f, // Bone_Tongue
    };
}
