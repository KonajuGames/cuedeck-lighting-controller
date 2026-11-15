#version 450

// One triangle covering the target, its texture coordinates running 0 to 1 across it, top row first.
layout(location = 0) out vec2 uv;

void main()
{
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    uv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
