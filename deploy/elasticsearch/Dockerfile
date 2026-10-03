# Elasticsearch + IK 中文分词镜像
#
# 目标（BUSINESS.md 15.2 / 用户需求 X1「使用 IK」）：索引用 IK 中文分词器。
#
# 历史（2026-10-02）：IK 装不上。当时 get.infini.cloud 的 SSL 握手被阻断
# （SSL peer shut down incorrectly），GitHub 上 infinilabs/analysis-ik 的 tag
# 只到 v1.10.6 / v5.0.0-rc1，没有 8.x 产物，所以按 PLAN.md 2.5 降级到内置 smartcn。
#
# 现状（2026-10-04 复测）：网络已恢复，get.infini.cloud 返回 200，IK 8.15.0 可正常安装，
# **已默认装 IK**，不再是降级。索引 mapping 里 analyzer = ik_max_word、
# search_analyzer = ik_smart（详见 ProductSearchIndex）。
#
# 仍要保留 INSTALL_IK 参数的理由：离线环境 / CI 镜像预构建时可能仍拿不到 IK，
# 那种情况下要能明确地退回 smartcn，而不是构建直接失败、连 ES 都起不来。
# 但**生产必须带 IK**——smartcn 会把「空气净化器」整体当成一个词，
# 用户搜「净化器」就搜不到，中文搜索基本不可用。
FROM docker.elastic.co/elasticsearch/elasticsearch:8.15.0

ARG INSTALL_IK=true
ARG IK_VERSION=8.15.0
ARG IK_URL=https://get.infini.cloud/elasticsearch/analysis-ik

# 装 IK 时不加 --batch 会卡在交互确认；网络不通时必须让构建**失败**，
# 否则会产出一个「以为装了 IK 其实没装」的镜像，表现为搜索中文搜不准，
# 而且要等到上线后才发现——比构建失败难查得多。
RUN if [ "$INSTALL_IK" = "true" ]; then \
      bin/elasticsearch-plugin install --batch "${IK_URL}/${IK_VERSION}"; \
    else \
      echo "警告：INSTALL_IK=false，退回内置 smartcn 分词器，中文搜索能力显著下降"; \
    fi