module @tiny_triton_kernel {
  tt.func public @fused_gemm_kernel(%a_ptr: !tt.ptr<f16>, %b_ptr: !tt.ptr<f16>, %c_ptr: !tt.ptr<f32>) {
    %pid = tt.get_program_id x : i32
    %offs = tt.make_range {end = 16 : i32, start = 0 : i32} : tensor<16xi32>
    %pa = tt.addptr %a_ptr, %offs : !tt.ptr<f16>, tensor<16xi32>
    %pb = tt.addptr %b_ptr, %offs : !tt.ptr<f16>, tensor<16xi32>
    %a = tt.load %pa : !tt.ptr<tensor<16x16xf16>>
    %b = tt.load %pb : !tt.ptr<tensor<16x16xf16>>
    %acc_init = arith.constant dense<0.0> : tensor<16x16xf32>
    %dot = tt.dot %a, %b, %acc_init : tensor<16x16xf16> * tensor<16x16xf16> -> tensor<16x16xf32>
    %bias = arith.constant dense<1.0> : tensor<16x16xf32>
    %res = arith.addf %dot, %bias : tensor<16x16xf32>
    tt.store %c_ptr, %res : !tt.ptr<tensor<16x16xf32>>
    tt.return
  }
}
