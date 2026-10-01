cmake_minimum_required(VERSION 3.21)
project(matee-wayland-presenter VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Qt6 REQUIRED COMPONENTS Gui)
find_package(LayerShellQt REQUIRED)

add_executable(matee-wayland-presenter main.cpp)
target_link_libraries(matee-wayland-presenter PRIVATE Qt6::Gui LayerShellQt::Interface)
target_compile_options(matee-wayland-presenter PRIVATE -Wall -Wextra -Wpedantic)

install(TARGETS matee-wayland-presenter RUNTIME DESTINATION bin)

include(CTest)
if(BUILD_TESTING)
    add_executable(matee-presenter-protocol-test protocol-test.cpp)
    target_link_libraries(matee-presenter-protocol-test PRIVATE Qt6::Gui)
    add_test(NAME presenter-protocol COMMAND matee-presenter-protocol-test)
endif()
