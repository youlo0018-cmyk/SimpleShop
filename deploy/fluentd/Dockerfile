# fluentd + ES 输出插件
# 官方 fluent/fluentd 镜像不含 fluent-plugin-elasticsearch，缺它会在解析配置时报
# "Unknown output plugin 'elasticsearch'" 并退出，因此这里补装。
FROM fluent/fluentd:v1.16-debian

USER root
RUN gem install fluent-plugin-elasticsearch --no-document
USER fluent

