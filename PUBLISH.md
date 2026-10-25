# 桌宠游戏守卫 · 上架 dsh 插件市场的步骤

DSH 生态的市场（你装的那个 `dshmarket`）收录方式是：**一个 GitHub 仓库 + 给
`awesome-dsh-plugin/awesome-dsh-plugin` 提一条 `data/plugins/<owner>__<repo>.yml`**。
所以「发布」= 把本目录推到你的 GitHub 仓库，然后提一个 PR。

## 0. 先决条件（一次性）

1. GitHub 账号（github.com 注册，邮箱验证完成）。
2. 仓库 `dsh-pet-game-guard`，Public。
3. 一个 classic PAT（Settings → Developer settings → Personal access tokens →
   Generate new token (classic)），只勾 `repo`。

> 市场 CI 要求**仓库创建满 1 天**才会收。今天建仓库+推代码，明天再提 PR 最稳。
> 提前提也不会封人，只是 CI 会报 repo age。

## 1. 建仓库并推送（需要 GITHUB_TOKEN）

```sh
cd dsh-pet-game-guard
GITHUB_TOKEN=ghp_xxx bash scripts/publish-github.sh <你的GitHub用户名> repo
```

脚本做的事（都可重复执行）：

- `POST /user/repos` 建一个 public 仓库 `dsh-pet-game-guard`（已存在则跳过）
- 用 token 作为 HTTPS 凭据 `git push`
- 给仓库打 `dsh-plugin` 等 topics

## 2. 给市场提 PR

```sh
GITHUB_TOKEN=ghp_xxx bash scripts/publish-github.sh <你的GitHub用户名> pr
```

脚本做的事：

- fork `awesome-dsh-plugin/awesome-dsh-plugin`（已 fork 则复用，等待 fork 就绪）
- clone 到临时目录，写 `data/plugins/<owner>__dsh-pet-game-guard.yml`
- 建分支、commit、push 到你的 fork
- `POST /repos/awesome-dsh-plugin/awesome-dsh-plugin/pulls` 开 PR

## 3. 手动版（不想给 token 时）

仓库建好后，把本目录推上去：

```sh
git init -b main
git add .
git commit -m "feat: dsh-pet-game-guard 0.1.0"
git remote add origin https://github.com/<你>/dsh-pet-game-guard.git
git push -u origin main
```

然后打开 <https://github.com/awesome-dsh-plugin/awesome-dsh-plugin/fork>，在你 fork 里
新建 `data/plugins/<你>__dsh-pet-game-guard.yml`：

```yaml
url: https://github.com/<你>/dsh-pet-game-guard
name: <你>/dsh-pet-game-guard
category: fun
description:
  en: Hides the dsh-pet windows while a game or fullscreen app is in the foreground and shows them again afterwards, without touching dsh-pet's configuration.
  zh: 打游戏或全屏时把 dsh-pet 的窗口隐藏起来，退出后原样显示，不改 dsh-pet 的任何配置。
tarball: https://github.com/<你>/dsh-pet-game-guard/releases/download/v0.1.0/dsh-pet-game-guard-0.1.0.tgz
```

最后在你 fork 的页面点 **Contribute → Open pull request**。

`tarball:` 是可选字段：不发 npm 时，市场会优先给用户预构建包。上传之前先在本目录跑：

```sh
"/c/Users/26363/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe" \
  "/c/Users/26363/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/pnpm/bin/pnpm.mjs" pack
```

把生成的 `dsh-pet-game-guard-0.1.0.tgz` 传到 GitHub Release `v0.1.0`。
