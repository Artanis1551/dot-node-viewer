module @tiny_tosa {
  func.func @main(%arg0: tensor<1x8x8x3x!quant.uniform<i8:f32, 0.05:128>>) -> (tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>) {
    %w = "tosa.const"() <{values = dense<1> : tensor<4x3x3x3xi8>}> : () -> tensor<4x3x3x3xi8>
    %b = "tosa.const"() <{values = dense<0> : tensor<4xi32>}> : () -> tensor<4xi32>
    %conv = tosa.conv2d %arg0, %w, %b {acc_type = i32, dilation = array<i64: 1, 1>, pad = array<i64: 1, 1, 1, 1>, stride = array<i64: 2, 2>} : (tensor<1x8x8x3x!quant.uniform<i8:f32, 0.05:128>>, tensor<4x3x3x3xi8>, tensor<4xi32>) -> tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>
    %clamp = tosa.clamp %conv {max_fp = 6.0 : f32, max_int = 127 : i64, min_fp = 0.0 : f32, min_int = -128 : i64} : (tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>) -> tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>
    %rescale = tosa.rescale %clamp {double_round = false, input_zp = 0 : i32, multiplier = array<i32: 1073741824>, output_zp = 128 : i32, per_channel = false, scale32 = true, shift = array<i8: 30>} : (tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>) -> tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>
    %pool = tosa.avg_pool2d %rescale {acc_type = i32, kernel = array<i64: 1, 1>, pad = array<i64: 0, 0, 0, 0>, stride = array<i64: 1, 1>} : (tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>) -> tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>
    func.return %pool : tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>
  }
}
