# Bundled mpv-compatible shaders

These files are unmodified upstream shader sources. HomePlayer loads them through libplacebo's `pl_mpv_user_shader_parse`; they are not Lua/JavaScript plugins and do not execute CPU code.

| Directory | Upstream source | Version | License |
| --- | --- | --- | --- |
| `anime4k` | https://github.com/bloc97/Anime4K | `7684e9586f8dcc738af08a1cdceb024cc184f426` | MIT |
| `artcnn` | https://github.com/Artoriuz/ArtCNN | `0837e5326e710f49e2ab0a8d995da0708047442e` | MIT |
| `cunny` | https://github.com/funnyplanter/CuNNy | `906031bb00c15dd6a6bbbaa21c0eb0b724ca8437` | LGPL-3.0-or-later |
| `fsrcnnx` | https://github.com/igv/FSRCNN-TensorFlow/releases/tag/1.1 | Release 1.1, `FSRCNNX_x2_8-0-4-1.glsl` | LGPL-3.0-or-later |
| `ravu`, `nnedi3` | https://github.com/bjin/mpv-prescalers | `b3f0a59d68f33b7162051ea5970a5169558f0ea2` | LGPL-3.0-or-later |
| `ssim` | https://gist.github.com/igv/2364ffa6e81540f29cb7ab4c9bc05b6b and https://gist.github.com/igv/36508af3ffc84410fe39761d6969be10 | Gist IDs in the source URLs | LGPL-3.0-or-later |
| `chroma/CfL_Prediction.glsl` | https://github.com/Artoriuz/glsl-chroma-from-luma-prediction | `066dff964b83aa34e893de25d590874bf8c8e94f` | MIT |
| `chroma/KrigBilateral.glsl` | https://gist.github.com/igv/a015fc885d5c22e6891820ad89555637 | Gist ID in the source URL | LGPL-3.0-or-later |
| `sharpening` | https://gist.github.com/igv/8a77e4eb8276753b54bb94c1c50c317e | Gist ID in the source URL | BSD-2-Clause |
| `restoration` | https://github.com/AN3223/dotfiles/tree/master/.config/mpv/shaders | `4413db6c1b47aa70ffce40c5a91158e518881718` | LGPL-2.1-or-later, per each shader header |

Full license texts are in `licenses`. Copyright notices also remain in the shader files. The AN3223 repository root license does not replace the LGPL notices carried by these shader sources.

Only a representative, bounded variant of each algorithm is bundled. Other sizes and experimental variants remain available upstream. All shader files are below HomePlayer's 1 MiB per-file limit.
