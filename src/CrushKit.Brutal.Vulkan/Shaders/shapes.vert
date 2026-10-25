#version 450
layout(location = 0) in vec2 position;
layout(location = 1) in vec4 color;
layout(push_constant) uniform Frame { vec2 extent; } frame;
layout(location = 0) out vec4 vertexColor;
void main()
{
    // Positive Vulkan viewport: framebuffer top-left is (-1,-1), not (-1,+1).
    gl_Position = vec4(2.0 * position / frame.extent - 1.0, 0.0, 1.0);
    vertexColor = color;
}
