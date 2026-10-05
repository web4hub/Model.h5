LLMS=$HOME/llms; SDK=$LLMS/rocm10-sdk; mkdir -p $SDK/tmp
git clone https://github.com/gufo-org/gufo $LLMS/gufo && git -C $LLMS/gufo checkout v0.5.0

uv venv --seed -p 3.12 $SDK/.venv
TMPDIR=$SDK/tmp PIP_NO_CACHE_DIR=1 $SDK/.venv/bin/python -m pip install \
  --index-url https://stable.repo.amd.com/rocm/whl-next/ "rocm[libraries,devel,device-gfx1151]==10.0.0"
$SDK/.venv/bin/rocm-sdk init

R=$($SDK/.venv/bin/rocm-sdk path --root)
export PATH="$R/bin:$PATH" HIP_PATH="$R" ROCM_PATH="$R"
cd $LLMS/gufo
cmake --preset release -B build/release-rocm10 \
  -DCMAKE_PREFIX_PATH="$R" -DCMAKE_HIP_COMPILER="$R/lib/llvm/bin/clang++" \
  "-DCMAKE_BUILD_RPATH=$R/lib;$R/lib/rocm_sysdeps/lib;$R/lib/llvm/lib"
cmake --build build/release-rocm10 -j 16
./build/release-rocm10/gufo diagnose
