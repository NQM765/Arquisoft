FROM python:3.13-slim

WORKDIR /app_dir

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

ENV PYTHONPATH=/app_dir:/app_dir/support-service:/app_dir/shared

WORKDIR /app_dir/support-service

CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
