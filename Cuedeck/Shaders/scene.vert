#version 450

// The 3D view's shapes: each instance's world and normal matrices, colour and glow from a storage buffer (drawn
// with firstInstance set to its index). Matches Scene3D.Push and Scene3D.Instance.
layout(push_constant) uniform Push
{
    mat4 viewProj;
    vec4 cameraPos;
    vec4 ambient;
    vec4 workDir;    // where the work light shines
    vec4 workColour; // its colour times its energy; black when off
} pc;

struct Instance
{
    mat4 model;
    mat4 normal;
    vec4 colour;
    vec4 extra; // x: glow
};

layout(std430, set = 0, binding = 0) readonly buffer Instances
{
    Instance items[];
};

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

layout(location = 0) out vec3 worldPos;
layout(location = 1) out vec3 normal;
layout(location = 2) out vec4 colour;
layout(location = 3) out float glow;

void main()
{
    Instance it = items[gl_InstanceIndex];
    vec4 world = it.model * vec4(inPosition, 1.0);
    worldPos = world.xyz;
    normal = mat3(it.normal) * inNormal;
    colour = it.colour;
    glow = it.extra.x;
    gl_Position = pc.viewProj * world;
}
