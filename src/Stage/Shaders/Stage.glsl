#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _MainTex_ST;
uniform vec4 _Gh3CamRight;
uniform vec4 _Gh3CamUp;
uniform vec4 _Gh3UvWibble;
uniform vec4 _Gh3UvPivot;
in vec4 in_POSITION0;
in vec3 in_NORMAL0;
in vec4 in_TANGENT0;
in vec4 in_COLOR0;
in vec2 in_TEXCOORD0;
in vec2 in_TEXCOORD1;
in vec2 in_TEXCOORD2;
in vec2 in_TEXCOORD3;
// Direct3D 11 shader model 4.0 has 16 vertex outputs. The surface model and the GH3 templates never run in the same draw,
// so on Direct3D 11 the surface values use the template registers.
#ifdef GH3_D3D11
#define surfaceColor gh3_T0
#define surfaceUv gh3_T1.xy
#define surfaceLightmapUv gh3_T1.zw
#define surfaceLayerUv gh3_T2.xy
#define surfaceMaskUv gh3_T2.zw
#define surfaceNormal gh3_T3.xyz
#else
out vec4 surfaceColor;
out vec2 surfaceUv;
out vec2 surfaceLightmapUv;
out vec2 surfaceLayerUv;
out vec2 surfaceMaskUv;
out vec3 surfaceNormal;
#endif
// GH3_TEMPLATE_VERTEX
void stage_main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    vec4 world = model * in_POSITION0;
    // GH3 Billboard_Fast: the normal holds the card pivot, and the corner offset is applied along the camera axes.
    if (_Gh3CamRight.w > 0.5)
    {
        vec3 offset = in_POSITION0.xyz - in_NORMAL0;
        world = model * vec4(in_NORMAL0, 1.0) + vec4(_Gh3CamRight.xyz * offset.x + _Gh3CamUp.xyz * offset.y, 0.0);
    }
    gl_Position = viewProjection * world;
    surfaceColor = in_COLOR0;
    vec2 uv = in_TEXCOORD0;
    // GH3 Wibble_Simple turns and scrolls the diffuse UV in its top-down V convention.
    if (_Gh3UvPivot.z > 0.5)
    {
        vec2 d = vec2(uv.x, 1.0 - uv.y) - _Gh3UvPivot.xy;
        vec2 turned = vec2(d.x * _Gh3UvWibble.x + d.y * _Gh3UvWibble.y, d.y * _Gh3UvWibble.x - d.x * _Gh3UvWibble.y) + _Gh3UvPivot.xy + _Gh3UvWibble.zw;
        uv = vec2(turned.x, 1.0 - turned.y);
    }
    surfaceUv = uv * _MainTex_ST.xy + _MainTex_ST.zw;
    surfaceLightmapUv = in_TEXCOORD1;
    surfaceLayerUv = in_TEXCOORD2;
    surfaceMaskUv = in_TEXCOORD3;
    surfaceNormal = mat3(model) * in_NORMAL0;
}
// _Gh3Template.x selects a translated GH3 material template; zero keeps the stage surface model above.
void main()
{
    if (_Gh3Template.x > 0.5) gh3_vertex_main();
    else stage_main();
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform sampler2D _MainTex;
uniform sampler2D _Gh3Lightmap;
uniform vec4 _Color;
uniform vec4 _Gh3Surface;
uniform sampler2D _Gh3LightData;
uniform vec4 _Gh3LightIndex;
uniform sampler2D _Gh3NormalTex;
uniform sampler2D _Gh3SpecularTex;
uniform sampler2D _Gh3LayerTex;
uniform sampler2D _Gh3MaskTex;
uniform vec4 _Gh3LayerColor;
uniform vec4 _Gh3Layers;
#ifdef GH3_D3D11
#define surfaceColor gh3_T0
#define surfaceUv gh3_T1.xy
#define surfaceLightmapUv gh3_T1.zw
#define surfaceLayerUv gh3_T2.xy
#define surfaceMaskUv gh3_T2.zw
#define surfaceNormal gh3_T3.xyz
#else
in vec4 surfaceColor;
in vec2 surfaceUv;
in vec2 surfaceLightmapUv;
in vec2 surfaceLayerUv;
in vec2 surfaceMaskUv;
in vec3 surfaceNormal;
#endif
layout(location = 0) out vec4 SV_Target0;
// Each lit surface owns one row: ambient, key direction, key color, then four SH coefficients.
vec4 Light(int column)
{
    vec2 size = vec2(textureSize(_Gh3LightData, 0));
    return texture(_Gh3LightData, (vec2(float(column), _Gh3LightIndex.x) + 0.5) / size);
}
// GH3_TEMPLATE_FRAGMENT
void stage_main()
{
    vec4 albedo = texture(_MainTex, surfaceUv) * _Color;
    vec4 color = albedo * surfaceColor;
    float mask = _Gh3Layers.y > 0.5 ? texture(_Gh3MaskTex, surfaceMaskUv).r : 1.0;
    // D_3ChkBox_Pass0_Pass1: the second texture replaces the diffuse by layer alpha x mask x vertex alpha; the output alpha stays the diffuse alpha.
    if (_Gh3Layers.x > 0.5)
    {
        vec4 layer = texture(_Gh3LayerTex, surfaceLayerUv) * _Gh3LayerColor;
        color.rgb = mix(color.rgb, surfaceColor.rgb * layer.rgb, clamp(layer.a * mask * surfaceColor.a, 0.0, 1.0));
    }
    else color.a *= mask;
    if (_Gh3Surface.y > 0.0 && color.a <= _Gh3Surface.y) discard;
    if (_Gh3Surface.x > 1.5)
    {
        vec3 normal = normalize(surfaceNormal);
        vec3 lightmap = _Gh3Surface.z > 0.0 ? texture(_Gh3Lightmap, surfaceLightmapUv).rgb : vec3(1.0);
        vec3 ambient = Light(3).xyz + Light(4).xyz * normal.x + Light(5).xyz * normal.y + Light(6).xyz * normal.z;
        vec4 keyColor = Light(2);
        float facing = clamp(dot(normal, -Light(1).xyz), 0.0, 1.0);
        vec3 light = ambient * lightmap + keyColor.xyz * facing * mix(vec3(1.0), lightmap, keyColor.w) + Light(0).xyz;
        color.rgb *= light;
    }
    else if (_Gh3Surface.x > 0.5) color.rgb *= Light(0).xyz;
    color.rgb *= _Gh3Surface.w;
    SV_Target0 = color;
}
void main()
{
    if (_Gh3Template.x > 0.5) SV_Target0 = gh3_fragment_main();
    else stage_main();
}
#endif
