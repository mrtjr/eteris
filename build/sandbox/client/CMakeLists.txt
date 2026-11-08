# DLL cliente do bootstrap da CEF (eteris_client.cpp). Compila contra a distribuição binária do PRÓPRIO motor
# (make_distrib do runtime em uso: mesmo commit do libcef.dll), com o libcef_dll_wrapper dela, CRT estática (/MT)
# e os /DELAYLOAD da CEF (CEF_SHARED_LINKER_FLAGS). Ver build.cmd.
cmake_minimum_required(VERSION 3.21)
project(eteris_client CXX)

# Barras normais: as macros da CEF voltam a interpretar o caminho, e as barras invertidas viram escapes.
file(TO_CMAKE_PATH "${CEF_ROOT}" CEF_ROOT)
if(NOT DEFINED CEF_ROOT OR NOT EXISTS "${CEF_ROOT}/cmake/FindCEF.cmake")
  message(FATAL_ERROR "CEF_ROOT tem de apontar para a distribuicao binaria da CEF (cef_binary_*_windows64*)")
endif()
set(ETERIS_CLIENT_NAME "Eteris.Browser" CACHE STRING "Nome da DLL (= nome do exe do bootstrap, sem extensao)")
set(ETERIS_APP_DLL "Eteris.App.dll" CACHE STRING "Montagem .NET que o processo principal arranca")

list(APPEND CMAKE_MODULE_PATH "${CEF_ROOT}/cmake")
find_package(CEF REQUIRED)
add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH} libcef_dll_wrapper)
ADD_LOGICAL_TARGET("libcef_lib" "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")

add_library(eteris_client SHARED eteris_client.cpp)
SET_LIBRARY_TARGET_PROPERTIES(eteris_client)
add_dependencies(eteris_client libcef_dll_wrapper)
target_compile_definitions(eteris_client PRIVATE "ETERIS_APP_DLL=L\"${ETERIS_APP_DLL}\"")
target_link_libraries(eteris_client libcef_lib libcef_dll_wrapper ${CEF_STANDARD_LIBS})
set_target_properties(eteris_client PROPERTIES OUTPUT_NAME "${ETERIS_CLIENT_NAME}")
