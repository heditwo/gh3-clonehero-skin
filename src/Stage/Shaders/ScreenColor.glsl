#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _MainTex_ST;
in vec4 in_POSITION0;
in vec2 in_TEXCOORD0;
out vec2 vs_TEXCOORD0;
void main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    gl_Position = viewProjection * model * in_POSITION0;
    vs_TEXCOORD0 = in_TEXCOORD0 * _MainTex_ST.xy + _MainTex_ST.zw;
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 _Gh3Red;
uniform vec4 _Gh3Green;
uniform vec4 _Gh3Blue;
layout(location = 0) uniform sampler2D _MainTex;
in vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
void main()
{
    vec4 source = texture(_MainTex, vs_TEXCOORD0);
    vec4 color = vec4(source.rgb, 1.0);
    SV_Target0 = vec4(dot(_Gh3Red, color), dot(_Gh3Green, color), dot(_Gh3Blue, color), source.a);
}
#endif
