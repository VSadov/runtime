set(CMAKE_INCLUDE_CURRENT_DIR ON)
set(GLOBALIZATION_NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.Globalization.Native)
set(COMPRESSON_NATIVE_DIR ${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/System.IO.Compression.Native)

# Suppress exporting of the PAL APIs
add_definitions(-DPALEXPORT=EXTERN_C)

include_directories("${GLOBALIZATION_NATIVE_DIR}")
include_directories("${COMPRESSON_NATIVE_DIR}")
include_directories("${CLR_REPO_ROOT_DIR}/src/libraries/Native/Unix/Common")

add_subdirectory(${GLOBALIZATION_NATIVE_DIR} System.Globalization.Native)

if(CLR_CMAKE_HOST_UNIX AND NOT CLR_CROSS_COMPONENTS_BUILD)
    add_subdirectory(${COMPRESSON_NATIVE_DIR} System.IO.Compression.Native)
endif()


