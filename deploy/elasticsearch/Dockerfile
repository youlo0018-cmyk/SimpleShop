# 自建 Elasticsearch 镜像
#
# 目标（BUSINESS.md 15.2）：用 IK 中文分词器。
# 实际情况（2026-10-02 实测）：IK 的 7.x/8.x 版本只在 get.infini.cloud 分发，
#   GitHub（infinilabs/analysis-ik）的 tag 只到 v1.10.6 / v5.0.0-rc1，没有 8.x 产物。
#   get.infini.cloud 在本环境 SSL 握手被阻断（SSL peer shut down incorrectly），
#   因此无法安装 IK 插件，按 PLAN.md 2.5 的降级方案改用 ES 内置的 smartcn 分词器。
#
# 恢复 IK 的方法：网络可达 get.infini.cloud 后，用下面的命令重建镜像
#   docker compose build --build-arg INSTALL_IK=true elasticsearch
# 之后在索引 mapping 里把 analyzer 从 smartcn 改为 ik_max_word / search_analyzer 改为 ik_smart。
# 切换分词器需要对存量索引 reindex，属于 S2 阶段的改动点，此处只负责镜像能力。

FROM docker.elastic.co/elasticsearch/elasticsearch:8.15.0

ARG INSTALL_IK=false
ARG IK_VERSION=8.15.0
ARG IK_URL=https://get.infini.cloud/elasticsearch/analysis-ik

RUN if [ "$INSTALL_IK" = "true" ]; then \
      bin/elasticsearch-plugin install --batch "${IK_URL}/${IK_VERSION}"; \
    else \
      echo "INSTALL_IK=false，跳过 IK 安装，使用内置 smartcn 分词器"; \
    fi

