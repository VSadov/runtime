set(CMAKE_INCLUDE_CURRENT_DIR ON)
set(GLOBALIZATION_NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.Globalization.Native)

set(IO_COMPRESSION_NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.IO.Compression.Native)
set(NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.Native)
set(NET_SECURITY_NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.Net.Security.Native)
set(SECURITY_CRYPTOGRAPHY_NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.Security.Cryptography.Native)

# Suppress exporting of the PAL APIs
add_definitions(-DPALEXPORT=EXTERN_C)

include_directories("${GLOBALIZATION_NATIVE_DIR}")
include_directories("${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/Common")

add_subdirectory(${GLOBALIZATION_NATIVE_DIR} System.Globalization.Native)

if(CLR_CMAKE_HOST_UNIX AND NOT CLR_CROSS_COMPONENTS_BUILD)
    add_subdirectory(${IO_COMPRESSION_NATIVE_DIR} System.IO.Compression.Native)
    add_subdirectory(${NATIVE_DIR} System.Native)
    add_subdirectory(${NET_SECURITY_NATIVE_DIR} System.Net.Security.Native)
    add_subdirectory(${SECURITY_CRYPTOGRAPHY_NATIVE_DIR} System.Security.Cryptography.Native)
endif()
