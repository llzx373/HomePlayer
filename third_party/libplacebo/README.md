# libplacebo 私有运行库

HomePlayer 使用未经修改的 MSYS2 MinGW x64 动态库。仅用于 D3D11 渲染后段，应用运行时从自己的 `libplacebo` 子目录加载，不安装到系统目录。

- 主库：`mingw-w64-x86_64-libplacebo-7.360.1-1`，`PL_API_VER=360`，`PL_HAVE_D3D11=1`，LGPL-2.1-or-later。
- 软件包：https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-libplacebo-7.360.1-1-any.pkg.tar.zst
- 软件包 SHA256：`004513d5f7a9ed0ae6bfee30ac26750768723a60681db9cebc6e6b7673ac3ac9`
- 对应源码与构建脚本：https://repo.msys2.org/mingw/sources/mingw-w64-libplacebo-7.360.1-1.src.tar.zst
- 上游源码：https://code.videolan.org/videolan/libplacebo/-/tree/v7.360.1

构建脚本使用 Meson，启用 `d3d11`、`lcms`、`libdovi`、`shaderc` 和 `vulkan`，禁用 demos，以 shared library 方式构建。HomePlayer 只调用 D3D11 后端，不创建 Vulkan 设备。

## 二进制校验

| 文件 | SHA256 |
| --- | --- |
| libplacebo-360.dll | `46b31fc5429f0b348d7c7183a805ba68d80170843eee2f35e8ac5962d6992e66` |
| libdovi.dll | `d0ebc0d98ac67dc28b176f77af125f2066dcb6cf6283f71c102d67f0eeb1df7c` |
| libgcc_s_seh-1.dll | `661f70dc19fde8c25bb56387b807b7a9ab7e532cf58fa8aeee078256c19d8980` |
| liblcms2-2.dll | `dc241cbdf2d295846ec564c1eb3c01639d43c0d72415a8758e65d71df31cc0ba` |
| liblcms2_fast_float-2.dll | `7a5412aa4333c0ccfc440e61de38da85a03bfaa6039383a0700b78e501cf93a4` |
| libshaderc_shared.dll | `803dd276af99afa40e96836484fea513852fba773fc45690c5eef5a5ebc3ae94` |
| libspirv-cross-c-shared.dll | `b769dd849d7dcbf7668ddbb55f66acbd0e26152da1112d796fbd0635c3aacd29` |
| libstdc++-6.dll | `b720a96289bcd6d9ec6f5f1f5612f1b1a523ce15d3ada20fe119791274cb512f` |
| libwinpthread-1.dll | `69d7bb54bfa7502501b8cda3e09ae9d0a7ae3325d7419aeaeb55c13f4ae8dddb` |
| vulkan-1.dll | `1d848aabe2673d89ccbced220cbc0b98793b49aa38e3b2d7b5a71e28f4875016` |

依赖来自同一 MSYS2 仓库的 `lcms2-2.19.1-1`、`libdovi-3.4.0-1`、`shaderc-2026.4-1`、`spirv-cross-1~1.4.363.0-1`、`vulkan-loader-1~1.4.363.0-1`、`gcc-libs-16.2.0-6`、`libstdc++-16.2.0-6` 和 `libwinpthread-14.0.0.r569.g34eeafb7d-1` 包。各组件许可证保存在 `licenses` 子目录；分发时保留全部 DLL 和该目录。
