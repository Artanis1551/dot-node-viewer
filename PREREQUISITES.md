# Development Machine Prerequisites (`dot-node-viewer`)

This document specifies all required SDKs, system libraries, Python toolchains, and desktop automation tools needed on the development machine (target baseline: **Linux Mint 22.2 / Ubuntu 24.04 Noble x86_64**) across the sequential build stages of `dot-node-viewer`.

---

## 1. .NET SDK & Workloads (Required from Stage S0)

- **.NET SDK:** `10.0.x` installed and available on `PATH` as `dotnet` (pinned via `global.json`, e.g., `10.0.100` or latest `10.0` feature band).
  - Verify with:
    ```bash
    dotnet --list-sdks
    ```
  - Install via system packages (`apt`) or Microsoft's official installer so `dotnet` is on `PATH`.
- **WebAssembly Workload (Required in Stage S12 for `DotNodeViewer.Browser`):**
  ```bash
  dotnet workload install wasm-tools
  ```
- **NativeAOT System Prerequisites (`PublishAot=true`, Required from Stage S1):**
  ```bash
  sudo apt-get update && sudo apt-get install -y clang zlib1g-dev libkrb5-dev
  ```
- **Documentation Website Generator (`docfx`, Required from Stage S0):**
  - Managed via the repo's `.config/dotnet-tools.json` local tool manifest (`dotnet tool restore` installs `docfx` locally so `dotnet tool run docfx build docs/site/docfx.json` and `dotnet tool run docfx docs/site/docfx.json --serve` work out of the box), or globally via:
    ```bash
    dotnet tool install -g docfx
    ```

---

## 2. Avalonia Desktop Runtime Dependencies (Required from Stage S5)

To run `DotNodeViewer.Desktop` and headless/live UI tests on Linux X11/XWayland:

```bash
sudo apt-get install -y \
  libx11-6 libx11-dev libice6 libsm6 libxcursor1 libxext6 \
  libxi6 libxinerama1 libxrandr2 libxrender1 libxtst6 \
  libfontconfig1 libfreetype6 libgl1-mesa-dri libegl1
```

---

## 3. Computer-Use Skill & Desktop UI Automation Prerequisites (Required for Stages S5–S6 & S11–S12)

The repository includes `.agents/skills/computer-use/SKILL.md` for background-first desktop inspection, accessibility tree verification, and interactive UI testing of `DotNodeViewer.Desktop`. Under the hood, it relies on **`cua-driver`** (`trycua/cua`) and Linux **AT-SPI 2** over D-Bus + X11/XWayland.

### 3.1 System Packages (AT-SPI 2 & X11 utilities)
```bash
sudo apt-get install -y \
  at-spi2-core libatspi2.0-0 libatk-bridge2.0-0 \
  python3-pyatspi wmctrl xdotool
```

### 3.2 Enable the Linux Accessibility Bus
Avalonia's accessibility peers (`AutomationProperties`) are only exposed to `cua-driver` when the desktop session's AT-SPI accessibility bus is enabled **before** launching the target app:

```bash
gsettings set org.gnome.desktop.interface toolkit-accessibility true
busctl --user set-property org.a11y.Bus /org/a11y/bus org.a11y.Status IsEnabled b true
```

Also ensure `DISPLAY` is exported in your session (e.g., `export DISPLAY=:0`).

### 3.3 Install & Verify `cua-driver` (and Hermes Computer-Use Bridge)
1. Install `cua-driver`:
   ```bash
   /bin/bash -c "$(curl -fsSL https://cua.ai/driver/install.sh)"
   ```
2. Optional skill pack & diagnostics:
   ```bash
   cua-driver doctor
   cua-driver skills install
   ```
   *(If running via the Hermes agent harness, run `hermes computer-use install` and `hermes computer-use doctor` to verify the MCP tool wrapper.)*

---

## 4. Python & Model-Generation Toolchains (Required in Format Stages)

Base test models (`<= 256 KB` committed fixtures) and ground-truth `.truth.json` files are generated deterministically by Python scripts under `tools/model-gen/`.

- **System Python:** `Python 3.12.x` (`python3 --version` on this machine is `3.12.3`), plus `python3-venv` and `python3-pip`:
  ```bash
  sudo apt-get install -y python3 python3-venv python3-pip curl jq
  ```

To avoid dependency conflicts between TensorFlow and PyTorch/ExecuTorch, model generation is split into isolated virtual environments (`tools/model-gen/<toolchain>/.venv`, git-ignored):

| Toolchain Directory | Introduced In | Pinned Packages (`requirements.txt`) |
|---|---|---|
| `tools/model-gen/onnx/` | **Stage S2** (ONNX) | `onnx==1.17.0`, `numpy==1.26.4`, `protobuf==5.29.3` |
| `tools/model-gen/tf/` | **Stage S3** (TFLite) & **Stage S10** (Keras) | `tensorflow-cpu==2.18.0`, `keras==3.8.0`, `h5py==3.12.1`, `numpy==1.26.4` |
| `tools/model-gen/torch/` | **Stage S7** (ExecuTorch) & **Stages S8–S9** (PyTorch / `.pt2`) | `torch==2.6.0` (CPU index `https://download.pytorch.org/whl/cpu`), `executorch==0.5.0`, `numpy==1.26.4` |

Setup template for each environment:
```bash
python3 -m venv tools/model-gen/<toolchain>/.venv
tools/model-gen/<toolchain>/.venv/bin/pip install --upgrade pip
tools/model-gen/<toolchain>/.venv/bin/pip install -r tools/model-gen/<toolchain>/requirements.txt
```

---

## 5. Fixture Staging Utilities (Required from Stage S2)

Used by `.agents/skills/add-test-model/scripts/stage-model.sh`:
- `bash` (>= 5.0)
- `curl`
- `sha256sum` (`coreutils`)
- `od`, `stat`, `awk`, `sed`, `tr` (`coreutils` / `gawk`)
