#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _Gh3Outline;
uniform vec4 _Gh3ViewZ;
uniform vec4 _Gh3Clock;
in vec4 in_POSITION0;
in vec3 in_NORMAL0;
void main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    float phase = fract(fract(_Gh3Outline.z * _Gh3Clock.x) + 0.5) * 6.283185482 - 3.141592741;
    float pulse = sin(phase) * 0.5 + 0.5;
    float depth = max(-dot(_Gh3ViewZ, model * in_POSITION0), 0.0);
    float width = mix(_Gh3Outline.x, depth * _Gh3Outline.w + _Gh3Outline.y, pulse);
    vec3 position = in_POSITION0.xyz;
    if (_Gh3Clock.z != 0.0 && depth > _Gh3Clock.y) position = vec3(0.0);
    gl_Position = viewProjection * model * vec4(position + in_NORMAL0 * width, in_POSITION0.w);
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 _Gh3Color;
layout(location = 0) out vec4 SV_Target0;
void main()
{
    SV_Target0 = _Gh3Color;
}
#endif
