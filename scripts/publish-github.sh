#!/usr/bin/env bash
# 把 dsh-pet-game-guard 推到 GitHub，并给 awesome-dsh-plugin 提收录 PR。
#
#   GITHUB_TOKEN=ghp_xxx bash scripts/publish-github.sh <owner> repo   # 建仓库 + push + topics
#   GITHUB_TOKEN=ghp_xxx bash scripts/publish-github.sh <owner> pr     # fork + 收录条目 + PR
#   GITHUB_TOKEN=ghp_xxx bash scripts/publish-github.sh <owner> all    # 两步都做
#
# 环境变量：
#   GITHUB_TOKEN  必填，classic PAT（repo 权限）或 fine-grained（Administration/Contents/Pull requests: write）
#   REPO_NAME     默认 dsh-pet-game-guard
#   UPSTREAM      默认 awesome-dsh-plugin/awesome-dsh-plugin

set -euo pipefail

OWNER="${1:?用法: publish-github.sh <owner> [repo|pr|all]}"
STEP="${2:-all}"
REPO_NAME="${REPO_NAME:-dsh-pet-game-guard}"
UPSTREAM="${UPSTREAM:-awesome-dsh-plugin/awesome-dsh-plugin}"
API="https://api.github.com"

: "${GITHUB_TOKEN:?需要设置 GITHUB_TOKEN}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

api() {
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -sS -X "$method" "$API$path" \
      -H "Authorization: Bearer $GITHUB_TOKEN" \
      -H "Accept: application/vnd.github+json" \
      -H "X-GitHub-Api-Version: 2022-11-28" \
      -d "$body"
  else
    curl -sS -X "$method" "$API$path" \
      -H "Authorization: Bearer $GITHUB_TOKEN" \
      -H "Accept: application/vnd.github+json" \
      -H "X-GitHub-Api-Version: 2022-11-28"
  fi
}

describe() {
  printf '%s' "$1" | sed -e 's/"\(message\|errors\)":/\n  \1:/g' | head -c 600
}

step_repo() {
  echo "==> 建仓库 $OWNER/$REPO_NAME"
  code=$(curl -sS -o /tmp/pgg-repo.json -w '%{http_code}' -X POST "$API/user/repos" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Accept: application/vnd.github+json" \
    -d "{\"name\":\"$REPO_NAME\",\"description\":\"打游戏时自动藏起 dsh-pet 桌宠，退出游戏再放回来（Windows）\",\"private\":false,\"has_issues\":true}")
  case "$code" in
    201) echo "    已创建" ;;
    422) echo "    已存在，跳过创建" ;;
    *) echo "    创建失败 HTTP $code"; describe "$(cat /tmp/pgg-repo.json)"; exit 1 ;;
  esac

  echo "==> 推送代码"
  cd "$HERE"
  if [ ! -d .git ]; then
    git init -b main >/dev/null
  fi
  git add -A
  if ! git diff --cached --quiet 2>/dev/null; then
    git -c user.name="$OWNER" -c user.email="$OWNER@users.noreply.github.com" \
      commit -q -m "feat: dsh-pet-game-guard 0.1.0" || true
  fi
  git remote remove origin 2>/dev/null || true
  git remote add origin "https://x-access-token:$GITHUB_TOKEN@github.com/$OWNER/$REPO_NAME.git"
  git -c user.name="$OWNER" -c user.email="$OWNER@users.noreply.github.com" push -u origin main

  echo "==> 设置 topics（市场 CI 要求有 dsh-plugin）"
  api PUT "/repos/$OWNER/$REPO_NAME/topics" \
    '{"names":["dsh-plugin","dsh","deepseek-harness","dsh-pet","desktop-pet","windows"]}' >/dev/null && echo "    已设置 dsh-plugin"
}

step_pr() {
  echo "==> fork $UPSTREAM"
  code=$(curl -sS -o /tmp/pgg-fork.json -w '%{http_code}' -X POST "$API/repos/$UPSTREAM/forks" \
    -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json")
  case "$code" in
    202|200) echo "    已发起 fork" ;;
    403) echo "    已有 fork，继续" ;;
    *) echo "    fork 返回 HTTP $code"; describe "$(cat /tmp/pgg-fork.json)" ;;
  esac

  echo "==> 等 fork 就绪"
  for i in $(seq 1 60); do
    if api GET "/repos/$OWNER/awesome-dsh-plugin" | grep -q '"full_name"'; then
      echo "    就绪"
      break
    fi
    sleep 5
    [ "$i" = 60 ] && { echo "    fork 超时"; exit 1; }
  done

  WORK="$(mktemp -d)"
  echo "==> clone fork 到 $WORK"
  git clone -q "https://x-access-token:$GITHUB_TOKEN@github.com/$OWNER/awesome-dsh-plugin.git" "$WORK/repo"
  cd "$WORK/repo"
  git checkout -q -b "add-$REPO_NAME"

  mkdir -p data/plugins
  ENTRY="data/plugins/${OWNER}__${REPO_NAME}.yml"
  cat > "$ENTRY" <<YAML
url: https://github.com/$OWNER/$REPO_NAME
name: $OWNER/$REPO_NAME
category: fun
description:
  en: Hides the dsh-pet windows while a game or fullscreen app is in the foreground and shows them again afterwards, without touching dsh-pet configuration.
  zh: 打游戏或全屏时把 dsh-pet 的窗口隐藏起来，退出后原样显示，不改 dsh-pet 的任何配置。
YAML

  git add "$ENTRY"
  git -c user.name="$OWNER" -c user.email="$OWNER@users.noreply.github.com" \
    commit -q -m "Add $OWNER/$REPO_NAME"
  git -c user.name="$OWNER" -c user.email="$OWNER@users.noreply.github.com" push -q -u origin "add-$REPO_NAME"

  echo "==> 开 PR"
  PR_BODY="新增插件 \`$OWNER/$REPO_NAME\`：打游戏/全屏时用 Win32 ShowWindow 把 dsh-pet 的窗口藏起来，退出后还原，不改 dsh-pet 配置。\n\n- 仓库：https://github.com/$OWNER/$REPO_NAME\n- Windows 专用，宿主侧只是一个进程生命周期包装，判定逻辑在随包分发的 PetGuard.exe（源码随包，见 assets/PetGuard.cs）"
  api POST "/repos/$UPSTREAM/pulls" "$(printf '{"title":"Add %s/%s","head":"%s:add-%s","base":"main","body":"%s"}' \
    "$OWNER" "$REPO_NAME" "$OWNER" "$REPO_NAME" "$PR_BODY")" | head -c 600
  echo
}

case "$STEP" in
  repo) step_repo ;;
  pr) step_pr ;;
  all) step_repo; step_pr ;;
  *) echo "未知步骤: $STEP"; exit 2 ;;
esac

echo "完成。"
