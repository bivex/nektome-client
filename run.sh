#!/usr/bin/env bash
# Build and run the NektoMe desktop client.
#
# Usage: ./run.sh [options]
#   (no options)   build Debug, then run
#   -n, --no-build skip the build step, just run
#   -r, --release  build and run in Release configuration
#   -t, --test     run unit tests before building
set -euo pipefail

cd "$(dirname "$0")"

PROJECT="src/NektoMe.Ui/NektoMe.Ui.csproj"
TESTS="tests/NektoMe.Application.Tests/NektoMe.Application.Tests.csproj"
CONFIG="Debug"
BUILD=1
TEST=0

for arg in "$@"; do
  case "$arg" in
    -n|--no-build) BUILD=0 ;;
    -r|--release)  CONFIG="Release" ;;
    -t|--test)     TEST=1 ;;
    *) echo "Unknown option: $arg" >&2
       echo "Usage: $0 [--no-build|-n] [--release|-r] [--test|-t]" >&2
       exit 1 ;;
  esac
done

if [ "$TEST" -eq 1 ]; then
  echo "==> Running tests..."
  dotnet test "$TESTS"
fi

if [ "$BUILD" -eq 1 ]; then
  echo "==> Building ($CONFIG)..."
  dotnet build "$PROJECT" -c "$CONFIG"
fi

echo "==> Starting NektoMe ($CONFIG)..."
exec dotnet run --project "$PROJECT" -c "$CONFIG" --no-build
