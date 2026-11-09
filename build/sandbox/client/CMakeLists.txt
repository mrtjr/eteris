# DLL cliente do bootstrap da CEF (eteris_client.cpp). Compila contra a distribuição binária do PRÓPRIO motor
# (make_distrib do runtime em uso: mesmo commit do libcef.dll), com o libcef_dll_wrapper dela, CRT estática (/MT)
# e os /DELAYLOAD da CEF (CEF_SHARED_LINKER_FLAGS). Ver build.cmd.
cmake_minimum_required(VERSION 3.21)
project(eteris_client CXX RC)

# Barras normais: as macros da CEF voltam a interpretar o caminho, e as barras invertidas viram escapes.
file(TO_CMAKE_PATH "${CEF_ROOT}" CEF_ROOT)
if(NOT DEFINED CEF_ROOT OR NOT EXISTS "${CEF_ROOT}/cmake/FindCEF.cmake")
  message(FATAL_ERROR "CEF_ROOT tem de apontar para a distribuicao binaria da CEF (cef_binary_*_windows64*)")
endif()
set(ETERIS_CLIENT_NAME "Eteris.Browser" CACHE STRING "Nome da DLL (= nome do exe do bootstrap, sem extensao)")
set(ETERIS_APP_DLL "Eteris.App.dll" CACHE STRING "Montagem .NET que o processo principal arranca")
# Só para provas ao vivo (docs/sandbox.md, «Rede de segurança»): ganchos de teste lidos do ambiente. A DLL fica marcada
# («test_hooks» em Comments) e build/sandbox/verify-client.ps1 recusa-a na publicação.
option(ETERIS_TEST_HOOKS "DLL de teste com ganchos lidos do ambiente (nunca publicar)" OFF)
set(ETERIS_CLIENT_FLAVOR "")
if(ETERIS_TEST_HOOKS)
  set(ETERIS_CLIENT_FLAVOR "; test_hooks")
endif()

# Sem o caminho de quem compila na DLL (docs/sandbox.md, «Caminhos de quem compila»). A DLL não leva PDB (a CEF só liga
# com /DEBUG em Debug), mas o __FILE__ dos LOG/CHECK do libcef_dll_wrapper e dos cabeçalhos da CEF fica como texto com o
# caminho completo da distribuição: 34 cadeias medidas na 0.2.14 (libcef_dll\ctocpp, cpptoc, wrapper), todas debaixo do
# CEF_ROOT. O /d1trimfile:<prefixo> do front-end do MSVC (c1xx, nas Build Tools 2022 e no VS 2026) corta esse prefixo do
# __FILE__. Um só prefixo, o mesmo em todas as unidades de compilação, e só no C++ (o rc.exe não o conhece). Sem a barra
# final: uma barra antes das aspas que o gerador põe à volta de um caminho com espaços escapá-las-ia. Depois de compilar,
# o build-all.ps1 confere a DLL (build/profile-paths.ps1) e pára se o caminho do perfil lá ficar.
# Não se usa o /pathmap do MSVC: só vale com /experimental:deterministic, que também não está documentado e acrescenta
# avisos de determinismo (C5048 com __DATE__/__TIME__, por exemplo) que o /WX da CEF tornaria erros.
string(REGEX REPLACE "/+$" "" _eteris_trim_prefix "${CEF_ROOT}")
file(TO_NATIVE_PATH "${_eteris_trim_prefix}" _eteris_trim_prefix)
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/d1trimfile:${_eteris_trim_prefix}>")

list(APPEND CMAKE_MODULE_PATH "${CEF_ROOT}/cmake")
find_package(CEF REQUIRED)
add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH} libcef_dll_wrapper)
ADD_LOGICAL_TARGET("libcef_lib" "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")

# Versão da CEF da distribuição gravada na DLL (recurso de versão: ProductVersion = CEF_VERSION, Comments com o commit):
# a publicação (build/sandbox/verify-client.ps1) compara-a com o manifest.json do runtime, para nunca juntar uma DLL de
# outra compilação do motor ao libcef.dll.
file(STRINGS "${CEF_ROOT}/include/cef_version.h" _cef_version_lines
     REGEX "^#define CEF_(VERSION|VERSION_MAJOR|VERSION_MINOR|VERSION_PATCH|COMMIT_NUMBER|COMMIT_HASH) ")
foreach(_line IN LISTS _cef_version_lines)
  if(_line MATCHES "^#define (CEF_[A-Z_]+) \"?([^\"]*)\"?$")
    set(${CMAKE_MATCH_1} "${CMAKE_MATCH_2}")
  endif()
endforeach()
if(NOT CEF_COMMIT_HASH MATCHES "^[0-9a-f]+$" OR NOT CEF_VERSION OR NOT CEF_COMMIT_NUMBER)
  message(FATAL_ERROR "cef_version.h sem CEF_VERSION/CEF_COMMIT_HASH/CEF_COMMIT_NUMBER: ${CEF_ROOT}")
endif()
configure_file(eteris_client.rc.in "${CMAKE_CURRENT_BINARY_DIR}/eteris_client.rc" @ONLY)

add_library(eteris_client SHARED eteris_client.cpp "${CMAKE_CURRENT_BINARY_DIR}/eteris_client.rc")
SET_LIBRARY_TARGET_PROPERTIES(eteris_client)
add_dependencies(eteris_client libcef_dll_wrapper)
target_compile_definitions(eteris_client PRIVATE "ETERIS_APP_DLL=L\"${ETERIS_APP_DLL}\"")
if(ETERIS_TEST_HOOKS)
  target_compile_definitions(eteris_client PRIVATE ETERIS_TEST_HOOKS)
endif()
target_link_libraries(eteris_client libcef_lib libcef_dll_wrapper ${CEF_STANDARD_LIBS})
set_target_properties(eteris_client PROPERTIES OUTPUT_NAME "${ETERIS_CLIENT_NAME}")
