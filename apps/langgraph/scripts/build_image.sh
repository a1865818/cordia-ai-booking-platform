#!/usr/bin/env bash
# Build a Docker image for the Cordia LangGraph server from langgraph.json.
#
# Usage: bash scripts/build_image.sh [tag] [platform]
#   tag      image tag        (default: cordia-langgraph)
#   platform target platform  (default: linux/amd64)
#
# Invoked by the `langgraph:docker-build` Nx target and the CI deploy pipeline.
set -euo pipefail

TAG="${1:-cordia-langgraph}"
PLATFORM="${2:-linux/amd64}"
CONFIG="langgraph.json"

echo "Building LangGraph image '${TAG}' for ${PLATFORM} from ${CONFIG}..."
uv run langgraph build -t "${TAG}" --platform "${PLATFORM}" -c "${CONFIG}"
echo "Built ${TAG}."
