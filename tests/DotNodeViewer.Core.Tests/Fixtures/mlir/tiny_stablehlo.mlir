module @tiny_stablehlo {
  func.func @main(%x: tensor<?x4xf32>, %cond: tensor<i1>) -> (tensor<?x4xf32>) {
    %w = stablehlo.constant dense_resource<fc_weights> : tensor<?x4xf32>
    %dot = stablehlo.dot_general %x, %w, contracting_dims = [1] x [0] : (tensor<?x4xf32>, tensor<2x4xf32>) -> tensor<?x4xf32>
    %bias = stablehlo.constant dense<[0.1, 0.2, 0.3, 0.4]> : tensor<4xf32>
    %add = stablehlo.add %dot, %bias : tensor<?x4xf32>
    %zero = stablehlo.constant dense<0.0> : tensor<?x4xf32>
    %relu = stablehlo.maximum %add, %zero : tensor<?x4xf32>
    %sel = scf.if %cond -> (tensor<?x4xf32>) {
      %neg = stablehlo.negate %relu : tensor<?x4xf32>
      scf.yield %neg : tensor<?x4xf32>
    } else {
      scf.yield %relu : tensor<?x4xf32>
    }
    func.return %sel : tensor<?x4xf32>
  }
}

{-#
  dialect_resources: {
    builtin: {
      fc_weights: "0x040000000000803f00000040000060c0000088400000003f0000c0bf0000204000004040"
    }
  }
#-}
