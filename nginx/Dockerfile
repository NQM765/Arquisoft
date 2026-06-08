FROM nginx:1.27-alpine

RUN apk add --no-cache openssl unzip

ARG CT_VERSION=0.39.1
ADD https://releases.hashicorp.com/consul-template/${CT_VERSION}/consul-template_${CT_VERSION}_linux_amd64.zip /tmp/consul-template.zip
RUN unzip /tmp/consul-template.zip -d /usr/local/bin/ && \
    rm /tmp/consul-template.zip && \
    chmod +x /usr/local/bin/consul-template

COPY nginx.ctmpl /etc/consul-template/nginx.ctmpl
COPY docker-entrypoint.d/10-generate-local-cert.sh /docker-entrypoint.d/10-generate-local-cert.sh
COPY docker-entrypoint.d/20-consul-template.sh /docker-entrypoint.d/20-consul-template.sh

RUN sed -i 's/\r$//' /docker-entrypoint.d/10-generate-local-cert.sh && \
    chmod +x /docker-entrypoint.d/10-generate-local-cert.sh && \
    sed -i 's/\r$//' /docker-entrypoint.d/20-consul-template.sh && \
    chmod +x /docker-entrypoint.d/20-consul-template.sh
