cmake_minimum_required(VERSION 3.5.0)
project(TabLinkHarmony)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_library(tablink_codec SHARED Decoder.cpp)
target_compile_options(tablink_codec PRIVATE -Wall -Wextra -Werror=return-type)
target_link_libraries(tablink_codec PUBLIC libace_napi.z.so libnative_media_codecbase.so
  libnative_media_core.so libnative_media_vdec.so libnative_window.so)
