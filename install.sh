#!/bin/sh
set -e

REPO="youngwoocho02/unity-cli"

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
case "$OS" in
  linux)  ;;
  darwin) ;;
  *)      echo "Unsupported OS: $OS (use Windows instructions in README)"; exit 1 ;;
esac

ARCH="$(uname -m)"
case "$ARCH" in
  x86_64|amd64)  ARCH="amd64" ;;
  aarch64|arm64)  ARCH="arm64" ;;
  *)              echo "Unsupported architecture: $ARCH"; exit 1 ;;
esac

INSTALL_DIR="$HOME/.local/bin"
mkdir -p "$INSTALL_DIR"

URL="https://github.com/${REPO}/releases/latest/download/unity-cli-${OS}-${ARCH}"

echo "Downloading unity-cli for ${OS}/${ARCH}..."
curl -fsSL "$URL" -o "$INSTALL_DIR/unity-cli"
chmod +x "$INSTALL_DIR/unity-cli"

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$HOME/.profile"
     export PATH="$INSTALL_DIR:$PATH"
     echo "Added $INSTALL_DIR to PATH (restart shell or run: source ~/.profile)" ;;
esac

echo "Installed unity-cli to $INSTALL_DIR/unity-cli"
unity-cli version
