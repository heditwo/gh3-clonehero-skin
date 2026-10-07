#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _Gh3SoftEdge;
uniform vec4 _Gh3CameraForward;
in vec4 in_POSITION0;
in vec3 in_NORMAL0;
in vec4 in_TANGENT0;
in vec4 in_COLOR0;
in vec2 in_TEXCOORD0;
in vec2 in_TEXCOORD1;
in vec2 in_TEXCOORD2;
out vec4 particleColor;
out vec2 particleUv;
out vec4 particleDepth;
out vec3 particleNormal;
out vec3 particleTangent;
out vec3 particleBitangent;
out vec3 particlePosition;
void main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    gl_Position = viewProjection * model * in_POSITION0;
    vec4 world = model * in_POSITION0;
    vec4 offsetPosition = viewProjection * vec4(world.xyz + _Gh3CameraForward.xyz * (_Gh3SoftEdge.y * in_TEXCOORD1.x), 1.0);
    vec3 screen = gl_Position.xyz / gl_Position.w * 0.5 + 0.5;
    particleDepth = vec4(screen.xy, abs((offsetPosition.z / offsetPosition.w * 0.5 + 0.5) - screen.z), screen.z);
    particleColor = in_COLOR0;
    particleUv = in_TEXCOORD0;
    particleNormal = mat3(model) * in_NORMAL0;
    particleTangent = mat3(model) * in_TANGENT0.xyz;
    particleBitangent = mat3(model) * vec3(in_TEXCOORD1.y, in_TEXCOORD2);
    particlePosition = world.xyz;
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform sampler2D _MainTex;
uniform sampler2D _CameraDepthTexture;
uniform sampler2D _Gh3NormalTex;
uniform sampler2D _Gh3SpecularTex;
uniform vec4 _Gh3Particle;
uniform vec4 _Gh3SoftEdge;
uniform vec4 _Gh3Lighting;
uniform vec4 _Gh3AmbientWeights;
uniform vec4 _Gh3LightDirection;
uniform vec4 _Gh3LightColor;
uniform vec4 _Gh3SH0;
uniform vec4 _Gh3SH1;
uniform vec4 _Gh3SH2;
uniform vec4 _Gh3SH3;
uniform vec4 _Gh3Maps;
uniform vec4 _Gh3Eye;
in vec4 particleColor;
in vec2 particleUv;
in vec4 particleDepth;
in vec3 particleNormal;
in vec3 particleTangent;
in vec3 particleBitangent;
in vec3 particlePosition;
layout(location = 0) out vec4 SV_Target0;
float specularPower(float value)
{
    return _Gh3Maps.w == 0.0 ? 1.0 : pow(clamp(value, 0.0, 1.0), _Gh3Maps.w);
}
void main()
{
    vec4 color = texture(_MainTex, particleUv) * particleColor;
    if (_Gh3Lighting.w != 0.0)
    {
        vec3 normal = particleNormal;
        if (_Gh3Maps.x != 0.0)
        {
            vec3 mapped = normalize(texture(_Gh3NormalTex, particleUv).xyz * 2.0 - 1.0);
            normal = particleTangent * mapped.x + particleBitangent * mapped.y + particleNormal * mapped.z;
        }
        vec3 front = _Gh3SH0.xyz + _Gh3SH1.xyz * normal.x + _Gh3SH2.xyz * normal.y + _Gh3SH3.xyz * normal.z;
        vec3 back = _Gh3SH0.xyz - _Gh3SH1.xyz * normal.x - _Gh3SH2.xyz * normal.y - _Gh3SH3.xyz * normal.z;
        vec3 ambient = vec3(0.0);
        if (_Gh3AmbientWeights.x > 0.0) ambient += front * _Gh3AmbientWeights.x;
        if (_Gh3AmbientWeights.y > 0.0) ambient += back * _Gh3AmbientWeights.y;
        float facing = dot(normal, -_Gh3LightDirection.xyz);
        float direct = clamp(facing, 0.0, 1.0) * _Gh3Lighting.y;
        if (_Gh3Lighting.z > 0.0 && facing <= 0.0) direct = clamp(-facing, 0.0, 1.0) * _Gh3Lighting.z;
        color.rgb *= ambient + max(vec3(_Gh3Lighting.x), _Gh3LightColor.xyz * direct);
        if (_Gh3Maps.y != 0.0)
        {
            vec3 halfway = normalize(normalize(_Gh3Eye.xyz - particlePosition) - _Gh3LightDirection.xyz);
            float specular = specularPower(dot(normal, halfway)) * _Gh3Lighting.y;
            if (facing <= 0.0)
                specular = _Gh3Lighting.z > 0.0 ? specularPower(dot(-normal, halfway)) * _Gh3Lighting.z : 0.0;
            vec3 specularColor = _Gh3LightColor.xyz * specular * particleColor.xyz;
            if (_Gh3Maps.z != 0.0) specularColor *= texture(_Gh3SpecularTex, particleUv).xyz;
            color.rgb += specularColor;
        }
    }
    if (_Gh3SoftEdge.x != 0.0)
        color.a *= clamp(abs(texture(_CameraDepthTexture, particleDepth.xy).r - particleDepth.w) / particleDepth.z, 0.0, 1.0);
    color.rgb *= max(1.0, _Gh3Particle.z);
    if (_Gh3Particle.x != 0.0) color = vec4(color.rgb * color.a, color.a * (1.0 - _Gh3Particle.x));
    if (_Gh3Particle.w != 0.0 && color.a <= _Gh3Particle.y) discard;
    SV_Target0 = color;
}
#endif
