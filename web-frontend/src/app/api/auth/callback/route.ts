import { NextRequest, NextResponse } from "next/server";

const PY_BACKEND_URL = process.env.PY_BACKEND_URL ?? "http://localhost:8000";

export async function GET(request: NextRequest) {
  const searchParams = request.nextUrl.searchParams;
  const code = searchParams.get("code");
  const state = searchParams.get("state");

  if (!code || !state) {
    return NextResponse.redirect(new URL("/login?error=missing_params", request.url));
  }

  try {
    const response = await fetch(
      `${PY_BACKEND_URL}/auth/oauth/complete?state=${state}&code=${code}`,
      { method: "POST" }
    );

    if (!response.ok) {
      console.error("OAuth completion failed", await response.text());
      return NextResponse.redirect(new URL("/login?error=oauth_failed", request.url));
    }

    return NextResponse.redirect(new URL("/login?success=oauth_completed", request.url));
  } catch (error) {
    console.error("Backend unreachable during OAuth callback", error);
    return NextResponse.redirect(new URL("/login?error=backend_unavailable", request.url));
  }
}