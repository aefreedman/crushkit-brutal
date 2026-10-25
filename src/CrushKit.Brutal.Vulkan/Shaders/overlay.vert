#version 450
layout(location = 0) in vec2 position;
layout(location = 1) in vec2 uv;
layout(location = 2) in vec4 color;
layout(push_constant) uniform Frame { vec2 extent; } frame;
layout(location = 0) out vec2 textureUv;
layout(location = 1) out vec4 vertexColor;
void main()
{
    gl_Position = vec4(2.0 * position / frame.extent - 1.0, 0.0, 1.0);
    textureUv = uv;
    vertexColor = color;
}
