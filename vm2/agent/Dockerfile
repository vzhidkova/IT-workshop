# Агент сбора логов Mattermost. Рядом должен лежать agent.py от Леры
# (с исправленным заголовком X-Api-Key).
FROM python:3.12-slim
WORKDIR /app
RUN pip install --no-cache-dir docker==7.1.0 requests==2.32.3
COPY agent.py .
CMD ["python", "-u", "agent.py"]
