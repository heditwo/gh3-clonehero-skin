#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _MainTex_ST;
in vec4 in_POSITION0;
in vec3 in_NORMAL0;
in vec4 in_COLOR0;
in vec2 in_TEXCOORD0;
in vec2 in_TEXCOORD1;
out vec4 surfaceColor;
out vec2 surfaceUv;
out vec2 surfaceLightmapUv;
out vec3 surfaceNormal;
void main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    gl_Position = viewProjection * model * in_POSITION0;
    surfaceColor = in_COLOR0;
    surfaceUv = in_TEXCOORD0 * _MainTex_ST.xy + _MainTex_ST.zw;
    surfaceLightmapUv = in_TEXCOORD1;
    surfaceNormal = mat3(model) * in_NORMAL0;
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
in vec4 surfaceColor;
in vec2 surfaceUv;
in vec2 surfaceLightmapUv;
in vec3 surfaceNormal;
layout(location = 0) out vec4 SV_Target0;
// Each lit surface owns one row: ambient, key direction, key color, then four SH coefficients.
vec4 Light(int column)
{
    vec2 size = vec2(textureSize(_Gh3LightData, 0));
    return texture(_Gh3LightData, (vec2(float(column), _Gh3LightIndex.x) + 0.5) / size);
}
void main()
{
    vec4 albedo = texture(_MainTex, surfaceUv) * _Color;
    vec4 color = albedo * surfaceColor;
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
#endif
