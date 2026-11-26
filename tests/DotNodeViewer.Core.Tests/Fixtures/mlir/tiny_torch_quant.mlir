module @tiny_torch_quant {
  func.func @forward(%x: !torch.vtensor<[1,8],f32>) -> (!torch.vtensor<[1,4],f32>) {
    %w_q = torch.vtensor.literal(dense<1> : tensor<8x4xsi8>) : !torch.vtensor<[8,4],si8>
    %scale = torch.constant.float 0.05
    %zp = torch.constant.int 0
    %qmin = torch.constant.int -128
    %qmax = torch.constant.int 127
    %dtype = torch.constant.int 1
    %xq = torch.quantized_decomposed.quantize_per_tensor %x, %scale, %zp, %qmin, %qmax, %dtype : !torch.vtensor<[1,8],f32>, f64, i64, i64, i64, i64 -> !torch.vtensor<[1,8],si8>
    %xdq = torch.quantized_decomposed.dequantize_per_tensor %xq, %scale, %zp, %qmin, %qmax, %dtype : !torch.vtensor<[1,8],si8>, f64, i64, i64, i64, i64 -> !torch.vtensor<[1,8],f32>
    %w_fp = torch.torchao.dequantize_affine %w_q, %scale, %zp : !torch.vtensor<[8,4],si8>, f64, i64 -> !torch.vtensor<[8,4],f32>
    %mm = torch.aten.mm %xdq, %w_fp : !torch.vtensor<[1,8],f32>, !torch.vtensor<[8,4],f32> -> !torch.vtensor<[1,4],f32>
    %out = torch.aten.relu %mm : !torch.vtensor<[1,4],f32> -> !torch.vtensor<[1,4],f32>
    func.return %out : !torch.vtensor<[1,4],f32>
  }
}
