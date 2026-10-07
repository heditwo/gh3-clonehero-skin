#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _Gh3TeslaAnimation;
uniform vec4 _Gh3TeslaShape;
in vec4 in_POSITION0;
in vec3 in_NORMAL0;
out vec4 teslaCoordinates;
out vec2 teslaShape;
void main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    gl_Position = viewProjection * model * in_POSITION0;
    teslaCoordinates = vec4(in_NORMAL0.x * _Gh3TeslaAnimation.z + _Gh3TeslaAnimation.x, in_NORMAL0.yz, _Gh3TeslaAnimation.y);
    teslaShape = _Gh3TeslaShape.xw * in_NORMAL0.z;
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform sampler2D _MainTex;
uniform vec4 _Gh3TeslaShape;
uniform vec4 _Gh3TeslaColor;
in vec4 teslaCoordinates;
in vec2 teslaShape;
layout(location = 0) out vec4 color;
void main()
{
    float noise = texture(_MainTex, vec2(teslaCoordinates.x, 1.0 - teslaCoordinates.w)).r;
    float center = 2.0 * (noise - .5) * teslaShape.x + .5;
    float edge = _Gh3TeslaShape.y - abs(center - teslaCoordinates.y);
    if (edge < 0.0) discard;
    float alpha = clamp((edge / _Gh3TeslaShape.y) / _Gh3TeslaShape.z, 0.0, 1.0);
    color = vec4(_Gh3TeslaColor.rgb, alpha >= 1.0 ? alpha : alpha * teslaShape.y);
}
#endif
