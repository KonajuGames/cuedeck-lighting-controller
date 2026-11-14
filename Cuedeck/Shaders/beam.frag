#version 450

// A beam: sLight's open cone, its colour (linear, alpha its brightness: 0.16 of its level, up to 0.3) fading from
// half at the lens to nothing at its far end, added over the room. Thicker haze shows it more (the haze slider at
// sLight's usual 0.3 leaves it as sLight drew it). Matches scene.vert's outputs and push constants.
layout(push_constant) uniform Push
{
    mat4 viewProj;
    vec4 cameraPos;
    vec4 ambient; // w: the haze's density per metre
    vec4 workDir;
    vec4 workColour;
} pc;

layout(location = 0) in vec3 worldPos;
layout(location = 1) in vec3 normal;
layout(location = 2) in vec4 colour;
layout(location = 3) in float glow;
layout(location = 4) in float along;

layout(location = 0) out vec4 outColor;

void main()
{
    float fade = 0.5 * clamp(0.5 - along, 0.0, 1.0);
    float haze = pc.ambient.w / 0.05;               // the slider, 0 to 1
    float shown = clamp(0.4 + 2.0 * haze, 0.4, 2.4); // 1 at 0.3
    outColor = vec4(colour.rgb * colour.a * fade * shown, 1.0);
}
