# Alternative LaTeX Rendering Using Textures

## Overview
This document outlines an alternative approach to rendering LaTeX in Godot using textures on plane meshes instead of the expensive Blender pipeline for converting SVG to 3D meshes. This approach could be 10-100x faster while still supporting per-character animations.

## Current Pipeline Issues
- Blender processing is expensive (slow)
- Requires external Blender installation
- Complex pipeline with multiple conversion steps
- Resource intensive for many LaTeX expressions

## Proposed Solution: SVG to Texture Rendering

### Basic Approach
1. Keep existing LaTeX → SVG conversion (using xelatex and dvisvgm)
2. Rasterize SVG to high-quality texture instead of converting to 3D mesh
3. Apply texture to plane mesh(es) in Godot
4. For per-character animations, use separate planes for each character

### Implementation Options

#### Option 1: Single Texture on Single Plane (Simplest)
